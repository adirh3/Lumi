using Lumi.Mobile.ViewModels;
using Lumi.Remote.Protocol;
using System.Threading.Channels;
using Xunit;

namespace Lumi.Mobile.Tests;

public sealed class CompactTranscriptTests
{
    [Fact]
    public async Task ActivityDisclosureLoadsDetailsOnlyWhenOpened()
    {
        var chatId = Guid.NewGuid();
        var sink = new ActivitySink
        {
            Details = new RemoteActivityDetails
            {
                ChatId = chatId,
                ActivityId = "activity-1",
                Tools =
                [
                    new RemoteToolCall
                    {
                        Id = "search",
                        Name = "web_search",
                        DisplayName = "Searched the web",
                        Category = "research",
                        Status = "Completed",
                        Input = "Avalonia mobile transcript",
                        Output = "Three sources"
                    },
                    new RemoteToolCall
                    {
                        Id = "test",
                        Name = "powershell",
                        DisplayName = "Ran tests",
                        Category = "verify",
                        Status = "Completed",
                        Output = "24 passed"
                    }
                ]
            }
        };
        var chat = new MobileChatViewModel(sink);
        chat.Reset(chatId, "Compact");
        chat.ApplyTranscript(new RemoteTranscript
        {
            ChatId = chatId,
            Revision = 1,
            Turns =
            [
                new RemoteTranscriptTurn
                {
                    Id = "turn-1",
                    Items =
                    [
                        new RemoteTranscriptItem
                        {
                            Id = "activity-row",
                            Kind = RemoteProtocol.ItemKinds.Activity,
                            ActivityId = "activity-1",
                            Status = "Completed",
                            ActionCount = 2,
                            DurationMs = 3_200,
                            FileChanges =
                            [
                                new RemoteFileChange
                                {
                                    Path = "src/Auth.cs",
                                    FileName = "Auth.cs",
                                    Operation = "Modified"
                                }
                            ]
                        }
                    ]
                }
            ]
        });

        var activity = Assert.IsType<ActivitySummaryItemViewModel>(
            Assert.Single(Assert.Single(chat.Turns).Items));
        Assert.Equal(0, sink.DetailRequests);
        Assert.False(activity.DetailsLoaded);
        Assert.Contains("2 actions", activity.SummaryText, StringComparison.Ordinal);
        Assert.Equal("1 file changed", activity.FileSummary);

        await activity.OpenCommand.ExecuteAsync(null);

        Assert.True(chat.IsActivitySheetOpen);
        Assert.Same(activity, chat.SelectedActivity);
        Assert.Equal(1, sink.DetailRequests);
        Assert.True(activity.DetailsLoaded);
        Assert.Collection(
            activity.Sections,
            section =>
            {
                Assert.Equal("Researched", section.Label);
                Assert.Single(section.Steps);
            },
            section =>
            {
                Assert.Equal("Verified", section.Label);
                Assert.Single(section.Steps);
            });

        activity.ToggleTechnicalDetailsCommand.Execute(null);
        Assert.True(activity.IsTechnicalDetailsVisible);
        Assert.All(
            activity.Sections.SelectMany(section => section.Steps),
            step => Assert.True(step.ShowTechnicalDetails));
    }

    [Fact]
    public void ActivitySummaryUpdatesInPlaceWhileRunning()
    {
        var chatId = Guid.NewGuid();
        var chat = new MobileChatViewModel(new ActivitySink());
        chat.Reset(chatId, "Compact");
        var initial = Transcript(chatId, "InProgress", 1, "Researching...");
        chat.ApplyTranscript(initial);

        var activity = Assert.IsType<ActivitySummaryItemViewModel>(
            Assert.Single(Assert.Single(chat.Turns).Items));
        Assert.True(activity.IsRunning);

        var completed = Transcript(chatId, "Completed", 3, "Activity");
        completed.Revision = 2;
        chat.ApplyTranscript(completed);

        Assert.Same(activity, Assert.Single(Assert.Single(chat.Turns).Items));
        Assert.False(activity.IsRunning);
        Assert.Contains("3 actions", activity.SummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedAndStoppedActivityStatesNeverLookSuccessful()
    {
        var failedSummary = new ActivitySummaryItemViewModel(new RemoteTranscriptItem
        {
            Id = "failed",
            Kind = RemoteProtocol.ItemKinds.Activity,
            Status = "Failed"
        });
        var stoppedStep = new ActivityStepViewModel(new RemoteToolCall
        {
            Id = "stopped",
            Name = "powershell",
            Status = "Stopped"
        });

        Assert.True(failedSummary.IsFailed);
        Assert.False(failedSummary.IsSucceeded);
        Assert.True(stoppedStep.IsStopped);
        Assert.False(stoppedStep.IsSucceeded);
    }

    [Fact]
    public async Task ActivityDetailsRetryWhenTheSummaryChangesDuringTheRequest()
    {
        var chatId = Guid.NewGuid();
        var sink = new BlockingActivitySink();
        var chat = new MobileChatViewModel(sink);
        chat.Reset(chatId, "Compact");
        chat.ApplyTranscript(Transcript(chatId, "InProgress", 1, "Researching..."));
        var activity = Assert.IsType<ActivitySummaryItemViewModel>(
            Assert.Single(Assert.Single(chat.Turns).Items));

        var open = activity.OpenCommand.ExecuteAsync(null);
        var first = await sink.NextRequestAsync();

        var updated = Transcript(
            chatId,
            "InProgress",
            1,
            "Researching...",
            detailVersion: 2);
        updated.Revision = 2;
        chat.ApplyTranscript(updated);
        first.TrySetResult(new RemoteActivityDetails
        {
            ChatId = chatId,
            ActivityId = "activity-id",
            Tools =
            [
                new RemoteToolCall
                {
                    Id = "old",
                    Name = "view",
                    DisplayName = "Old detail",
                    Category = "research"
                }
            ]
        });
        await open;

        var second = await sink.NextRequestAsync();
        second.TrySetResult(new RemoteActivityDetails
        {
            ChatId = chatId,
            ActivityId = "activity-id",
            Tools =
            [
                new RemoteToolCall
                {
                    Id = "new-1",
                    Name = "view",
                    DisplayName = "Current detail",
                    Category = "research"
                },
                new RemoteToolCall
                {
                    Id = "new-2",
                    Name = "edit",
                    DisplayName = "New action",
                    Category = "work"
                }
            ]
        });

        await WaitUntilAsync(() => activity.DetailsLoaded);
        Assert.Equal(2, sink.DetailRequests);
        Assert.DoesNotContain(
            activity.Sections.SelectMany(section => section.Steps),
            step => step.DisplayName == "Old detail");
        Assert.Contains(
            activity.Sections.SelectMany(section => section.Steps),
            step => step.DisplayName == "New action");
    }

    [Fact]
    public void OpenActivityRebindsByStableIdentityWhenTheTranscriptWindowRolls()
    {
        var chatId = Guid.NewGuid();
        var chat = new MobileChatViewModel(new ActivitySink());
        chat.Reset(chatId, "Rolling window");
        chat.ApplyTranscript(new RemoteTranscript
        {
            ChatId = chatId,
            Revision = 1,
            Turns =
            [
                new RemoteTranscriptTurn
                {
                    Id = "turn-0",
                    Items =
                    [
                        new RemoteTranscriptItem
                        {
                            Id = "other",
                            Kind = RemoteProtocol.ItemKinds.Assistant,
                            Text = "Older"
                        }
                    ]
                },
                new RemoteTranscriptTurn
                {
                    Id = "turn-1",
                    Items =
                    [
                        new RemoteTranscriptItem
                        {
                            Id = "activity-old-row",
                            Kind = RemoteProtocol.ItemKinds.Activity,
                            ActivityId = "stable-activity",
                            Status = "InProgress",
                            ActionCount = 1
                        }
                    ]
                }
            ]
        });
        var original = Assert.IsType<ActivitySummaryItemViewModel>(
            Assert.Single(chat.Turns[1].Items));
        original.IsTechnicalDetailsVisible = true;
        chat.SelectedActivity = original;
        chat.IsActivitySheetOpen = true;

        chat.ApplyTranscript(new RemoteTranscript
        {
            ChatId = chatId,
            Revision = 2,
            Turns =
            [
                new RemoteTranscriptTurn
                {
                    Id = "turn-0",
                    Items =
                    [
                        new RemoteTranscriptItem
                        {
                            Id = "activity-new-row",
                            Kind = RemoteProtocol.ItemKinds.Activity,
                            ActivityId = "stable-activity",
                            Status = "Completed",
                            ActionCount = 2
                        }
                    ]
                }
            ]
        });

        Assert.True(chat.IsActivitySheetOpen);
        Assert.NotNull(chat.SelectedActivity);
        Assert.NotSame(original, chat.SelectedActivity);
        Assert.Equal("stable-activity", chat.SelectedActivity!.ActivityId);
        Assert.True(chat.SelectedActivity.IsTechnicalDetailsVisible);
        Assert.False(chat.SelectedActivity.IsRunning);
    }

    [Fact]
    public void SourcesOpenAsOneAnswerOwnedDisclosureAndRebindAcrossRefresh()
    {
        var chatId = Guid.NewGuid();
        var chat = new MobileChatViewModel(new ActivitySink());
        chat.Reset(chatId, "Sources");
        chat.ApplyTranscript(SourceTranscript(
            chatId,
            revision: 1,
            title: "Original source",
            url: "https://www.example.com/original"));
        var answer = Assert.IsType<AssistantItemViewModel>(
            Assert.Single(Assert.Single(chat.Turns).Items));

        Assert.True(answer.HasSources);
        Assert.Equal("1 source", answer.SourceCountText);
        Assert.Equal("example.com", answer.SourceSummary);
        answer.OpenSourcesCommand.Execute(null);

        Assert.True(chat.IsSourcesSheetOpen);
        Assert.Same(answer, chat.SelectedSourceAnswer);

        chat.ApplyTranscript(SourceTranscript(
            chatId,
            revision: 2,
            title: "Updated source",
            url: "https://docs.example.net/current"));

        Assert.True(chat.IsSourcesSheetOpen);
        Assert.NotSame(answer, chat.SelectedSourceAnswer);
        Assert.Equal("Updated source", Assert.Single(chat.SelectedSourceAnswer!.Sources).Title);
        Assert.Equal("docs.example.net", chat.SelectedSourceAnswer.SourceSummary);

        Assert.True(chat.DismissTopmostSheet());
        Assert.False(chat.IsSourcesSheetOpen);
    }

    private static RemoteTranscript Transcript(
        Guid chatId,
        string status,
        int actions,
        string label,
        long detailVersion = 1) =>
        new()
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
                            Id = "activity",
                            Kind = RemoteProtocol.ItemKinds.Activity,
                            ActivityId = "activity-id",
                            Status = status,
                            ActionCount = actions,
                            Label = label,
                            DetailVersion = detailVersion
                        }
                    ]
                }
            ]
        };

    private static RemoteTranscript SourceTranscript(
        Guid chatId,
        long revision,
        string title,
        string url) =>
        new()
        {
            ChatId = chatId,
            Revision = revision,
            Turns =
            [
                new RemoteTranscriptTurn
                {
                    Id = $"turn-{revision}",
                    Items =
                    [
                        new RemoteTranscriptItem
                        {
                            Id = "stable-assistant",
                            Kind = RemoteProtocol.ItemKinds.Assistant,
                            Text = "Answer",
                            Sources =
                            [
                                new RemoteSource
                                {
                                    Title = title,
                                    Snippet = "Supporting detail",
                                    Url = url
                                }
                            ]
                        }
                    ]
                }
            ]
        };

    private sealed class ActivitySink : IRemoteCommandSink, IRemoteActivityDetailSink
    {
        public RemoteActivityDetails? Details { get; init; }
        public int DetailRequests { get; private set; }

        public Task<RemoteCommandResult> SendCommandAsync(RemoteCommand command) =>
            Task.FromResult(new RemoteCommandResult { Ok = true, RequestId = command.RequestId });

        public Task<RemoteUploadResponse> UploadAsync(
            string fileName,
            ReadOnlyMemory<byte> content) =>
            Task.FromResult(new RemoteUploadResponse { Ok = true, Path = fileName });

        public Task<RemoteActivityDetails?> GetActivityDetailsAsync(
            Guid chatId,
            string activityId)
        {
            DetailRequests++;
            return Task.FromResult(Details);
        }
    }

    private sealed class BlockingActivitySink : IRemoteCommandSink, IRemoteActivityDetailSink
    {
        private readonly Channel<TaskCompletionSource<RemoteActivityDetails?>> _requests =
            Channel.CreateUnbounded<TaskCompletionSource<RemoteActivityDetails?>>();

        public int DetailRequests { get; private set; }

        public Task<RemoteCommandResult> SendCommandAsync(RemoteCommand command) =>
            Task.FromResult(new RemoteCommandResult { Ok = true });

        public Task<RemoteUploadResponse> UploadAsync(
            string fileName,
            ReadOnlyMemory<byte> content) =>
            Task.FromResult(new RemoteUploadResponse { Ok = true, Path = fileName });

        public Task<RemoteActivityDetails?> GetActivityDetailsAsync(
            Guid chatId,
            string activityId)
        {
            DetailRequests++;
            var completion = new TaskCompletionSource<RemoteActivityDetails?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _requests.Writer.TryWrite(completion);
            return completion.Task;
        }

        public Task<TaskCompletionSource<RemoteActivityDetails?>> NextRequestAsync() =>
            _requests.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The expected compact transcript state was not reached.");
            await Task.Delay(10);
        }
    }
}
