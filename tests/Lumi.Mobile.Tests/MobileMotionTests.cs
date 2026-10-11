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
                        Id = "server-user", Kind = RemoteProtocol.ItemKinds.User, Text = "A new thought"
                    },
                    Answer("reply")
                ]
            });
            chat.ApplyTranscript(transcript);
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
        public TaskCompletionSource<RemoteCommandResult> Response { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<RemoteCommandResult> SendCommandAsync(RemoteCommand command) =>
            HoldSend && command.Action == RemoteProtocol.Actions.SendMessage
                ? Response.Task
                : Task.FromResult(new RemoteCommandResult { Ok = true });

        public Task<RemoteUploadResponse> UploadAsync(string fileName, ReadOnlyMemory<byte> content) =>
            throw new NotSupportedException();
    }
}
