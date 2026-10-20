using Avalonia.Threading;
using Lumi.Mobile.ViewModels;
using Lumi.Remote.Protocol;
using Xunit;

namespace Lumi.Mobile.Tests;

[Collection("Headless mobile UI")]
public sealed class InlineImageResolutionTests
{
    [Fact]
    public async Task UnchangedRefreshKeepsResolvedImagesAndTheirLeases()
    {
        using var session = HeadlessMobileSession.Start();
        Exception? failure = null;
        await session.Dispatch(() =>
        {
            try
            {
                var sink = new ImageSink("/cache/local.png");
                var chat = new MobileChatViewModel(sink);
                var transcript = ImageTranscript(Guid.NewGuid(), Guid.NewGuid());
                chat.Reset(transcript.ChatId, "Images");
                chat.ApplyTranscript(transcript);
                Dispatcher.UIThread.RunJobs();
                var answer = Assert.IsType<AssistantItemViewModel>(chat.Turns[0].Items[0]);
                var changes = 0;
                answer.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(AssistantItemViewModel.Text))
                        changes++;
                };
                transcript.Revision++;
                chat.ApplyTranscript(transcript);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("![Local](/cache/local.png)", answer.Text);
                Assert.Equal(1, sink.RequestCount);
                Assert.Equal(0, changes);
                Assert.Null(sink.LastRelease);
                chat.Reset(Guid.NewGuid(), "Next chat");
                Assert.NotNull(sink.LastRelease);
            }
            catch (Exception ex) { failure = ex; }
        }, CancellationToken.None);
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public async Task UnchangedRefreshDoesNotCancelAnImageDownloadInFlight()
    {
        using var session = HeadlessMobileSession.Start();
        Exception? failure = null;
        await session.Dispatch(async () =>
        {
            var sink = new DeferredImageSink();
            var chat = new MobileChatViewModel(sink);
            try
            {
                var transcript = ImageTranscript(Guid.NewGuid(), Guid.NewGuid());
                chat.Reset(transcript.ChatId, "Images");
                chat.ApplyTranscript(transcript);
                transcript.Revision++;
                chat.ApplyTranscript(transcript);
                Assert.Equal(1, sink.RequestCount);
                Assert.False(sink.RequestToken.IsCancellationRequested);
                sink.Response.SetResult("/cache/local.png");
                await Task.Yield();
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("![Local](/cache/local.png)",
                    Assert.IsType<AssistantItemViewModel>(chat.Turns[0].Items[0]).Text);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                sink.Response.TrySetResult("/cache/local.png");
                chat.Reset(Guid.NewGuid(), "Next chat");
            }
        }, CancellationToken.None);
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public async Task AnUnresolvedImageCanRetryOnTheNextRefresh()
    {
        using var session = HeadlessMobileSession.Start();
        Exception? failure = null;
        await session.Dispatch(() =>
        {
            try
            {
                var sink = new ImageSink(null);
                var chat = new MobileChatViewModel(sink);
                var transcript = ImageTranscript(Guid.NewGuid(), Guid.NewGuid());
                chat.Reset(transcript.ChatId, "Images");
                chat.ApplyTranscript(transcript);
                Dispatcher.UIThread.RunJobs();
                sink.Path = "/cache/local.png";
                transcript.Revision++;
                chat.ApplyTranscript(transcript);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(2, sink.RequestCount);
                Assert.Equal("![Local](/cache/local.png)",
                    Assert.IsType<AssistantItemViewModel>(chat.Turns[0].Items[0]).Text);
                chat.Reset(Guid.NewGuid(), "Next chat");
            }
            catch (Exception ex) { failure = ex; }
        }, CancellationToken.None);
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public async Task ChangedImageDescriptorsReleaseTheOldLeaseAndResolveTheReplacement()
    {
        using var session = HeadlessMobileSession.Start();
        Exception? failure = null;
        await session.Dispatch(() =>
        {
            try
            {
                var sink = new ImageSink("/cache/first.png");
                var chat = new MobileChatViewModel(sink);
                var transcript = ImageTranscript(Guid.NewGuid(), Guid.NewGuid());
                chat.Reset(transcript.ChatId, "Images");
                chat.ApplyTranscript(transcript);
                Dispatcher.UIThread.RunJobs();
                sink.Path = "/cache/replacement.png";
                transcript.Turns[0].Items[0].InlineImages![0].FileName = "replacement.png";
                transcript.Revision++;
                chat.ApplyTranscript(transcript);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(2, sink.RequestCount);
                Assert.NotNull(sink.LastRelease);
                Assert.Equal("![Local](/cache/replacement.png)",
                    Assert.IsType<AssistantItemViewModel>(chat.Turns[0].Items[0]).Text);
                chat.Reset(Guid.NewGuid(), "Next chat");
            }
            catch (Exception ex) { failure = ex; }
        }, CancellationToken.None);
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public async Task PartialFailureRetriesOnlyTheMissingImageWithoutResettingTheVisibleOne()
    {
        using var session = HeadlessMobileSession.Start();
        Exception? failure = null;
        await session.Dispatch(() =>
        {
            try
            {
                var sink = new SelectiveImageSink();
                var chat = new MobileChatViewModel(sink);
                var transcript = ImageTranscript(Guid.NewGuid(), Guid.NewGuid());
                transcript.Turns[0].Items[0].Text += "\n![Second](C:\\Images\\second.png)";
                transcript.Turns[0].Items[0].InlineImages!.Add(
                    new RemoteInlineImage { Index = 1, FileName = "second.png" });
                chat.Reset(transcript.ChatId, "Images");
                chat.ApplyTranscript(transcript);
                Dispatcher.UIThread.RunJobs();
                var answer = Assert.IsType<AssistantItemViewModel>(chat.Turns[0].Items[0]);
                Assert.Contains("![Local](/cache/first.png)", answer.Text);
                Assert.Contains("![Second](C:\\Images\\second.png)", answer.Text);
                sink.SecondAvailable = true;
                transcript.Revision++;
                chat.ApplyTranscript(transcript);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal([0, 1, 1], sink.Requests);
                Assert.Contains("![Local](/cache/first.png)", answer.Text);
                Assert.Contains("![Second](/cache/second.png)", answer.Text);
                Assert.DoesNotContain(0, sink.Releases);
                chat.Reset(Guid.NewGuid(), "Next chat");
                Assert.Equal(1, sink.Releases.Count(index => index == 0));
                Assert.Equal(1, sink.Releases.Count(index => index == 1));
            }
            catch (Exception ex) { failure = ex; }
        }, CancellationToken.None);
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

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

    [Fact]
    public async Task SwitchingChatsReleasesResolvedImageLeases()
    {
        using var session = HeadlessMobileSession.Start();
        var chatId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var sink = new ImageSink("/data/user/0/com.lumi.mobile/cache/local.png");
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
            Dispatcher.UIThread.RunJobs();

            chat.Reset(Guid.NewGuid(), "Other chat");
        }, CancellationToken.None);

        Assert.Equal((chatId, messageId, 0), sink.LastRelease);
    }

    private static RemoteTranscript ImageTranscript(Guid chatId, Guid messageId) => new()
    {
        ChatId = chatId, Revision = 1,
        Turns = [new RemoteTranscriptTurn
        {
            Id = "turn",
            Items = [new RemoteTranscriptItem
            {
                Id = messageId.ToString("N"), Kind = RemoteProtocol.ItemKinds.Assistant,
                Text = "![Local](C:\\Images\\local.png)",
                InlineImages = [new RemoteInlineImage { Index = 0, FileName = "local.png" }]
            }]
        }]
    };

    private sealed class ImageSink(string? path) : IRemoteCommandSink, IRemoteMarkdownImageSink
    {
        public string? Path { get; set; } = path;
        public int RequestCount { get; private set; }
        public (Guid ChatId, Guid MessageId, int Index) LastRequest { get; private set; }
        public (Guid ChatId, Guid MessageId, int Index)? LastRelease { get; private set; }

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
            RequestCount++;
            LastRequest = (chatId, messageId, imageIndex);
            return Task.FromResult(Path);
        }

        public void ReleaseMarkdownImages(
            Guid chatId,
            Guid messageId,
            IReadOnlyList<RemoteInlineImage> images)
        {
            if (images.Count > 0)
                LastRelease = (chatId, messageId, images[0].Index);
        }
    }

    private sealed class SelectiveImageSink : IRemoteCommandSink, IRemoteMarkdownImageSink
    {
        public bool SecondAvailable { get; set; }
        public List<int> Requests { get; } = [];
        public List<int> Releases { get; } = [];

        public Task<RemoteCommandResult> SendCommandAsync(RemoteCommand command) =>
            throw new NotSupportedException();

        public Task<RemoteUploadResponse> UploadAsync(string fileName, ReadOnlyMemory<byte> content) =>
            throw new NotSupportedException();

        public Task<string?> DownloadMarkdownImageAsync(Guid chatId, Guid messageId, int imageIndex,
            string fileName, CancellationToken cancellationToken)
        {
            Requests.Add(imageIndex);
            return Task.FromResult<string?>(imageIndex == 0
                ? "/cache/first.png"
                : SecondAvailable ? "/cache/second.png" : null);
        }

        public void ReleaseMarkdownImages(Guid chatId, Guid messageId, IReadOnlyList<RemoteInlineImage> images) =>
            Releases.AddRange(images.Select(image => image.Index));
    }

    private sealed class DeferredImageSink : IRemoteCommandSink, IRemoteMarkdownImageSink
    {
        public int RequestCount { get; private set; }
        public CancellationToken RequestToken { get; private set; }
        public TaskCompletionSource<string?> Response { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<RemoteCommandResult> SendCommandAsync(RemoteCommand command) =>
            throw new NotSupportedException();

        public Task<RemoteUploadResponse> UploadAsync(string fileName, ReadOnlyMemory<byte> content) =>
            throw new NotSupportedException();

        public Task<string?> DownloadMarkdownImageAsync(Guid chatId, Guid messageId, int imageIndex,
            string fileName, CancellationToken cancellationToken)
        {
            RequestCount++;
            RequestToken = cancellationToken;
            return Response.Task.WaitAsync(cancellationToken);
        }

        public void ReleaseMarkdownImages(Guid chatId, Guid messageId, IReadOnlyList<RemoteInlineImage> images)
        {
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

        public void ReleaseMarkdownImages(
            Guid chatId,
            Guid messageId,
            IReadOnlyList<RemoteInlineImage> images)
        {
        }
    }
}
