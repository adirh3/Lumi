using Avalonia.Threading;
using Lumi.Mobile.ViewModels;
using Lumi.Remote.Protocol;
using Xunit;

namespace Lumi.Mobile.Tests;

[Collection("Headless mobile UI")]
public sealed class InlineImageResolutionTests
{
    [Fact]
    public async Task LocalDesktopImageTargetsAreRewrittenToThePhoneCache()
    {
        using var session = HeadlessMobileSession.Start();
        var chatId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        string? answerText = null;
        (Guid ChatId, Guid MessageId, int Index)? request = null;
        await session.Dispatch(() =>
        {
            var sink = new ImageSink("/data/user/0/com.lumi.mobile/cache/local.png");
            var chat = new MobileChatViewModel(sink);
            chat.Reset(chatId, "Images");
            chat.ApplyTranscript(new RemoteTranscript
            {
                ChatId = chatId,
                Revision = 1,
                Turns =
                [
                    new RemoteTranscriptTurn
                    {
                        Id = "turn",
                        Items =
                        [
                            new RemoteTranscriptItem
                            {
                                Id = messageId.ToString("N"),
                                Kind = RemoteProtocol.ItemKinds.Assistant,
                                Text = "![Local](C:\\Images\\local.png)",
                                InlineImages =
                                [
                                    new RemoteInlineImage
                                    {
                                        Index = 0,
                                        FileName = "local.png"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            });
            Dispatcher.UIThread.RunJobs();

            answerText = ((AssistantItemViewModel)chat.Turns[0].Items[0]).Text;
            request = sink.LastRequest;
        }, CancellationToken.None);

        Assert.Equal(
            "![Local](/data/user/0/com.lumi.mobile/cache/local.png)",
            answerText);
        Assert.Equal((chatId, messageId, 0), request);
    }

    [Fact]
    public async Task StreamingDeltasContinueFromTheOriginalMarkdownOffsets()
    {
        using var session = HeadlessMobileSession.Start();
        var chatId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        const string source = "![Local](C:\\Images\\local.png)";
        string? finalText = null;
        bool applied = false;
        await session.Dispatch(() =>
        {
            var chat = new MobileChatViewModel(
                new ImageSink("/data/user/0/com.lumi.mobile/cache/local.png"));
            chat.Reset(chatId, "Images");
            chat.ApplyTranscript(Transcript(source));
            Dispatcher.UIThread.RunJobs();

            applied = chat.ApplyDelta(new RemoteStreamDelta
            {
                ChatId = chatId,
                ItemId = messageId.ToString("N"),
                Offset = source.Length,
                Text = " tail"
            });
            finalText = ((AssistantItemViewModel)chat.Turns[0].Items[0]).Text;

            RemoteTranscript Transcript(string text) => new()
            {
                ChatId = chatId,
                Revision = 1,
                Turns =
                [
                    new RemoteTranscriptTurn
                    {
                        Id = "turn",
                        Items =
                        [
                            new RemoteTranscriptItem
                            {
                                Id = messageId.ToString("N"),
                                Kind = RemoteProtocol.ItemKinds.Assistant,
                                Text = text,
                                InlineImages =
                                [
                                    new RemoteInlineImage
                                    {
                                        Index = 0,
                                        FileName = "local.png"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            };
        }, CancellationToken.None);

        Assert.True(applied);
        Assert.Equal(source + " tail", finalText);
    }

    [Fact]
    public async Task SwitchingChatsCancelsRemovedRowsImageDownloads()
    {
        using var session = HeadlessMobileSession.Start();
        var chatId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var sink = new BlockingImageSink();
        await session.Dispatch(() =>
        {
            var chat = new MobileChatViewModel(sink);
            chat.Reset(chatId, "Images");
            chat.ApplyTranscript(new RemoteTranscript
            {
                ChatId = chatId,
                Revision = 1,
                Turns =
                [
                    new RemoteTranscriptTurn
                    {
                        Id = "turn",
                        Items =
                        [
                            new RemoteTranscriptItem
                            {
                                Id = messageId.ToString("N"),
                                Kind = RemoteProtocol.ItemKinds.Assistant,
                                Text = "![Local](C:\\Images\\local.png)",
                                InlineImages =
                                [
                                    new RemoteInlineImage
                                    {
                                        Index = 0,
                                        FileName = "local.png"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            });
            chat.Reset(Guid.NewGuid(), "Other chat");
        }, CancellationToken.None);

        await sink.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private sealed class ImageSink(string path) : IRemoteCommandSink, IRemoteMarkdownImageSink
    {
        public (Guid ChatId, Guid MessageId, int Index) LastRequest { get; private set; }

        public Task<RemoteCommandResult> SendCommandAsync(RemoteCommand command) =>
            Task.FromResult(new RemoteCommandResult { Ok = true });

        public Task<RemoteUploadResponse> UploadAsync(
            string fileName,
            ReadOnlyMemory<byte> content) =>
            Task.FromResult(new RemoteUploadResponse { Ok = true, Path = fileName });

        public Task<string?> DownloadMarkdownImageAsync(
            Guid chatId,
            Guid messageId,
            int imageIndex,
            string fileName,
            CancellationToken cancellationToken)
        {
            LastRequest = (chatId, messageId, imageIndex);
            return Task.FromResult<string?>(path);
        }
    }

    private sealed class BlockingImageSink : IRemoteCommandSink, IRemoteMarkdownImageSink
    {
        public TaskCompletionSource Canceled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<RemoteCommandResult> SendCommandAsync(RemoteCommand command) =>
            Task.FromResult(new RemoteCommandResult { Ok = true });

        public Task<RemoteUploadResponse> UploadAsync(
            string fileName,
            ReadOnlyMemory<byte> content) =>
            Task.FromResult(new RemoteUploadResponse { Ok = true, Path = fileName });

        public async Task<string?> DownloadMarkdownImageAsync(
            Guid chatId,
            Guid messageId,
            int imageIndex,
            string fileName,
            CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return null;
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                Canceled.TrySetResult();
                throw;
            }
        }
    }
}
