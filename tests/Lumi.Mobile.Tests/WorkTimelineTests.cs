using System.Text.Json;
using Lumi.Mobile.ViewModels;
using Lumi.Remote.Protocol;
using Xunit;

namespace Lumi.Mobile.Tests;

public sealed class WorkTimelineTests
{
    [Fact]
    public void RecordedQaExpandedWorkContainsPreamblesRatherThanLeavingThemAsTranscriptSiblings()
    {
        var chatId = Guid.Parse("8a9abc0c-67cb-498c-8ac1-1003220be758");
        var chat = new MobileChatViewModel(new TimelineSink());
        chat.Reset(chatId, "QA Mobile Timeline and Git");
        var transcript = RecordedQaTranscript(chatId);
        chat.ApplyTranscript(transcript);
        var turn = chat.Turns[0];
        var work = Assert.IsType<WorkSummaryItemViewModel>(turn.DisplayItems[1]);
        Assert.Equal("Work 58s", work.Label);
        Assert.False(work.IsExpanded);
        Assert.Empty(work.Items);
        Assert.Equal(3, turn.DisplayItems.Count);

        // The live simulator was left in exactly this expanded state. These rows must now be
        // children of one Work component, not siblings that look like independent preambles.
        work.ToggleCommand.Execute(null);
        Assert.Equal(3, turn.DisplayItems.Count);
        Assert.Same(work, turn.DisplayItems[1]);
        Assert.Equal(turn.Items.Skip(1).SkipLast(1), work.Items);
        Assert.Equal(new[] { "QA PREAMBLE ONE\n", "QA PREAMBLE TWO\n" },
            work.Items.OfType<AssistantItemViewModel>().Select(item => item.Text));
        Assert.Equal("QA FINAL ANSWER", Assert.IsType<AssistantItemViewModel>(turn.DisplayItems[^1]).Text);
        Assert.Equal(new[] { "user", "error" }, chat.Turns[1].DisplayItems.Select(item => item.Kind));

        transcript.Revision++;
        chat.ApplyTranscript(transcript);
        Assert.True(work.IsExpanded);
        Assert.Equal(3, turn.DisplayItems.Count);
        Assert.Equal(5, work.Items.Count);
        work.ToggleCommand.Execute(null);
        Assert.Empty(work.Items);
        Assert.Equal(3, turn.DisplayItems.Count);
    }

    [Fact]
    public void ActiveRowsStayChronologicalAndStreamInPlaceUntilAuthoritativeCompletion()
    {
        var chatId = Guid.NewGuid();
        var sink = new TimelineSink();
        var chat = new MobileChatViewModel(sink);
        chat.Reset(chatId, "Timeline");
        var transcript = Timeline(chatId, completed: false);
        chat.ApplyTranscript(transcript);
        var turn = Assert.Single(chat.Turns);
        Assert.Equal(new[] { "user", "preamble", "research", "interim", "verify", "answer" },
            turn.DisplayItems.Select(item => item.Id));
        var answer = Assert.IsType<AssistantItemViewModel>(turn.Items[^1]);
        Assert.True(chat.ApplyDelta(new RemoteStreamDelta
        {
            ChatId = chatId, ItemId = "answer", Offset = answer.Text.Length, Text = " continues"
        }));
        Assert.Same(answer, turn.DisplayItems[^1]);
        Assert.Equal("Final answer continues", answer.Text);
        Assert.Equal(0, sink.DetailRequests);

        // Status can arrive before the authoritative transcript: do not guess completion from idle.
        chat.ApplyStatus(new RemoteChatStatus { ChatId = chatId });
        Assert.DoesNotContain(turn.DisplayItems, item => item is WorkSummaryItemViewModel);
        var completed = Timeline(chatId, completed: true);
        completed.Revision = 2;
        completed.Turns[0].Items[^1].Text = answer.Text;
        chat.ApplyTranscript(completed);

        Assert.Same(answer, turn.DisplayItems[^1]);
        Assert.Equal("Final answer continues", answer.Text);
        Assert.Equal(new[] { "user", "work-preamble", "answer" }, turn.DisplayItems.Select(item => item.Id));
        Assert.False(answer.IsStreaming);
        Assert.Equal(0, sink.DetailRequests);
    }

    [Theory]
    [InlineData(3_200, "Work 3s")]
    [InlineData(91_000, "Work 1m 31s")]
    public async Task CompletionCollapsesPreamblesAndExpandReviewsOriginalChronologyWithoutFetchingHistory(
        double duration, string expectedLabel)
    {
        var chatId = Guid.NewGuid();
        var sink = new TimelineSink();
        var chat = new MobileChatViewModel(sink);
        chat.Reset(chatId, "Timeline");
        var transcript = Timeline(chatId, completed: true);
        transcript.Turns[0].WorkDurationMs = duration;
        chat.ApplyTranscript(transcript);
        var turn = Assert.Single(chat.Turns);
        var work = Assert.IsType<WorkSummaryItemViewModel>(turn.DisplayItems[1]);
        Assert.Equal(expectedLabel, work.Label);
        Assert.False(work.IsExpanded);
        var collapsedPath = work.DisclosurePath;
        Assert.DoesNotContain(turn.DisplayItems, item => item.Id is "preamble" or "interim");
        var original = turn.Items.ToArray();

        work.ToggleCommand.Execute(null);
        Assert.True(work.IsExpanded);
        Assert.NotEqual(collapsedPath, work.DisclosurePath);
        Assert.Equal(original, VisibleChronology(turn));
        Assert.Equal(new[] { "user", "work-preamble", "answer" }, turn.DisplayItems.Select(item => item.Id));
        Assert.Equal(new[] { "preamble", "research", "interim", "verify" }, work.Items.Select(item => item.Id));
        Assert.Equal(0, sink.DetailRequests);

        await Assert.IsType<ActivitySummaryItemViewModel>(
            work.Items.Single(item => item.Id == "verify")).OpenCommand.ExecuteAsync(null);
        Assert.Equal(1, sink.DetailRequests);
        Assert.Equal("verify-detail", sink.LastActivityId);
        Assert.True(chat.IsActivitySheetOpen);

        work.ToggleCommand.Execute(null);
        Assert.Equal(collapsedPath, work.DisclosurePath);
        Assert.Equal(new[] { "user", "work-preamble", "answer" }, turn.DisplayItems.Select(item => item.Id));
        Assert.Equal(original, turn.Items);
        Assert.Empty(work.Items);
    }

    [Fact]
    public void ExpandedActivityDetailsDoNotReorderActionsByCategory()
    {
        var activity = new ActivitySummaryItemViewModel(new RemoteTranscriptItem
        {
            Id = "activity", Kind = RemoteProtocol.ItemKinds.Activity
        });
        activity.ApplyDetails(new RemoteActivityDetails
        {
            Tools =
            [
                new RemoteToolCall { Id = "read", Category = "research" },
                new RemoteToolCall { Id = "edit", Category = "work" },
                new RemoteToolCall { Id = "reread", Category = "research" },
                new RemoteToolCall { Id = "test", Category = "verify" }
            ]
        });
        Assert.Equal(new[] { "read", "edit", "reread", "test" },
            activity.Sections.SelectMany(section => section.Steps).Select(step => step.Id));
    }

    [Fact]
    public void CollapseLeavesUserAttachmentsQuestionErrorsAndGeneratedFilesUsable()
    {
        var chatId = Guid.NewGuid();
        var chat = new MobileChatViewModel(new TimelineSink());
        chat.Reset(chatId, "Important rows");
        var transcript = Timeline(chatId, completed: true);
        var remote = transcript.Turns[0];
        remote.Items[0].Attachments = [new RemoteAttachment { Path = "input.txt", FileName = "input.txt" }];
        remote.Items.InsertRange(3,
        [
            new RemoteTranscriptItem
            {
                Id = "question", Kind = RemoteProtocol.ItemKinds.Question,
                Question = new RemoteQuestion
                {
                    QuestionId = "question-id", Text = "Choose", Options = ["A", "B"]
                }
            },
            new RemoteTranscriptItem { Id = "error", Kind = RemoteProtocol.ItemKinds.Error, Text = "Recovered issue" },
            new RemoteTranscriptItem
            {
                Id = "file", Kind = RemoteProtocol.ItemKinds.File,
                Attachments = [new RemoteAttachment
                {
                    MessageId = Guid.NewGuid(), FileName = "result.txt", Path = "result.txt"
                }]
            }
        ]);
        chat.ApplyTranscript(transcript);
        var turn = Assert.Single(chat.Turns);
        Assert.Equal(new[] { "user", "work-preamble", "question", "error", "file", "answer" },
            turn.DisplayItems.Select(item => item.Id));
        Assert.Single(Assert.IsType<UserTurnItemViewModel>(turn.DisplayItems[0]).Attachments);
        Assert.Equal("question-id", Assert.IsType<QuestionItemViewModel>(turn.DisplayItems[2]).QuestionId);
        Assert.Equal("Recovered issue", Assert.IsType<ErrorItemViewModel>(turn.DisplayItems[3]).Text);
        Assert.NotNull(Assert.Single(Assert.IsType<FileItemViewModel>(turn.DisplayItems[4]).Files).MessageId);

        var protectedRows = turn.DisplayItems.Where(item =>
            item is QuestionItemViewModel or ErrorItemViewModel or FileItemViewModel).ToArray();
        var work = Assert.IsType<WorkSummaryItemViewModel>(turn.DisplayItems[1]);
        work.ToggleCommand.Execute(null);
        Assert.Equal(new[] { "user", "work-preamble", "question", "error", "file", "answer" },
            turn.DisplayItems.Select(item => item.Id));
        Assert.Equal(protectedRows, turn.DisplayItems.Where(item =>
            item is QuestionItemViewModel or ErrorItemViewModel or FileItemViewModel));
        Assert.Equal(new[] { "preamble", "research", "interim", "verify" }, work.Items.Select(item => item.Id));
        Assert.DoesNotContain(work.Items, item =>
            item is UserTurnItemViewModel or QuestionItemViewModel or ErrorItemViewModel or FileItemViewModel);
    }

    [Fact]
    public void ReconnectPreservesFrozenTimingExpansionAndFinalAnswerIdentity()
    {
        var chatId = Guid.NewGuid();
        var chat = new MobileChatViewModel(new TimelineSink());
        chat.Reset(chatId, "History");
        var transcript = Timeline(chatId, completed: true);
        transcript.RevisionEpoch = "first-server";
        transcript.Revision = 80;
        transcript.IsLatestWindow = false;
        transcript.HasLaterMessages = true;
        chat.ApplyTranscript(transcript);
        var turn = Assert.Single(chat.Turns);
        var work = Assert.IsType<WorkSummaryItemViewModel>(turn.DisplayItems[1]);
        work.ToggleCommand.Execute(null);

        var restored = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(transcript, RemoteJsonContext.Default.RemoteTranscript),
            RemoteJsonContext.Default.RemoteTranscript)!;
        restored.RevisionEpoch = "restarted-server";
        restored.Revision = 1;
        restored.Status.IsBusy = true;
        chat.ApplyTranscript(restored);
        Assert.Same(work, turn.DisplayItems[1]);
        Assert.True(work.IsExpanded);
        Assert.Equal("Work 1m 31s", work.Label);
        Assert.Equal("answer", turn.DisplayItems[^1].Id);
        Assert.Equal("Final answer", Assert.IsType<AssistantItemViewModel>(turn.DisplayItems[^1]).Text);
    }

    [Fact]
    public void InterruptedOrLegacyTurnsNeverHideTheirOnlyAssistantResponse()
    {
        var chatId = Guid.NewGuid();
        var chat = new MobileChatViewModel(new TimelineSink());
        chat.Reset(chatId, "Stopped");
        var transcript = Timeline(chatId, completed: false);
        transcript.Status.IsBusy = false;
        transcript.Turns[0].Items.RemoveAt(5);
        transcript.Turns[0].Items[4].Status = "Stopped";
        transcript.Turns[0].Items.Add(new RemoteTranscriptItem
        {
            Id = "error", Kind = RemoteProtocol.ItemKinds.Error, Text = "Canceled"
        });
        chat.ApplyTranscript(transcript);
        var turn = Assert.Single(chat.Turns);
        Assert.Equal(turn.Items, turn.DisplayItems);
        Assert.Contains(turn.DisplayItems, item => item.Id == "preamble");
        Assert.True(Assert.IsType<ActivitySummaryItemViewModel>(turn.DisplayItems[4]).IsStopped);
        Assert.IsType<ErrorItemViewModel>(turn.DisplayItems[^1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeltaFindsCanonicalHiddenRowsAndOffsetMismatchDoesNotAlterDisclosure(bool expanded)
    {
        var chatId = Guid.NewGuid();
        var chat = new MobileChatViewModel(new TimelineSink());
        chat.Reset(chatId, "Late stream");
        chat.ApplyTranscript(Timeline(chatId, completed: true));
        var turn = Assert.Single(chat.Turns);
        var preamble = Assert.IsType<AssistantItemViewModel>(turn.Items[1]);
        var work = Assert.IsType<WorkSummaryItemViewModel>(turn.DisplayItems[1]);
        if (expanded)
            work.ToggleCommand.Execute(null);
        var delta = new RemoteStreamDelta
        {
            ChatId = chatId, ItemId = "preamble", Offset = preamble.Text.Length + 1, Text = " more"
        };
        Assert.False(chat.ApplyDelta(delta));
        Assert.Same(work, turn.DisplayItems[1]);
        Assert.Equal(expanded, work.IsExpanded);

        delta.Offset = preamble.Text.Length;
        Assert.True(chat.ApplyDelta(delta));
        Assert.Equal("Checking first more", preamble.Text);
        Assert.True(preamble.IsStreaming);
        Assert.Equal(turn.Items, turn.DisplayItems);
        Assert.Equal("answer", turn.DisplayItems[^1].Id);
        Assert.Empty(work.Items);
    }

    private static RemoteTranscript Timeline(Guid chatId, bool completed) => new()
    {
        ChatId = chatId,
        Revision = 1,
        TotalRawMessageCount = 6,
        WindowEndMessageIndex = 6,
        Status = new RemoteChatStatus { ChatId = chatId, IsBusy = !completed },
        Turns =
        [
            new RemoteTranscriptTurn
            {
                Id = "turn",
                FinalAnswerId = completed ? "answer" : null,
                WorkDurationMs = completed ? 91_000 : null,
                Items =
                [
                    new RemoteTranscriptItem { Id = "user", Kind = "user", Text = "Do it" },
                    new RemoteTranscriptItem { Id = "preamble", Kind = "assistant", Text = "Checking first" },
                    new RemoteTranscriptItem
                    {
                        Id = "research", Kind = "activity", ActivityId = "research-detail",
                        ActionCount = 2, Status = "Completed", DurationMs = 1_000
                    },
                    new RemoteTranscriptItem { Id = "interim", Kind = "assistant", Text = "Now verifying" },
                    new RemoteTranscriptItem
                    {
                        Id = "verify", Kind = "activity", ActivityId = "verify-detail",
                        ActionCount = 1, Status = "Completed", DurationMs = 3_000
                    },
                    new RemoteTranscriptItem
                    {
                        Id = "answer", Kind = "assistant", Text = "Final answer", IsStreaming = !completed
                    }
                ]
            }
        ]
    };

    private static IEnumerable<TranscriptItemViewModel> VisibleChronology(TranscriptTurnViewModel turn) =>
        turn.DisplayItems.SelectMany(item => item is WorkSummaryItemViewModel work
            ? (IEnumerable<TranscriptItemViewModel>)work.Items
            : new[] { item });

    internal static RemoteTranscript RecordedQaTranscript(Guid chatId)
    {
        // Display-relevant fields of the real compact response from desktop PID 77232.
        var transcript = JsonSerializer.Deserialize("""
            {
              "revision": 1, "windowEndMessageIndex": 9, "totalRawMessageCount": 9,
              "isLatestWindow": true, "status": { "isBusy": false, "isStreaming": false },
              "turns": [
                {
                  "id": "turn-0", "finalAnswerId": "43a1dc625cd643d689fd7646ac2d1ce2",
                  "workDurationMs": 58261.5555,
                  "items": [
                    { "id": "17f3a3d779514c9f91b0c93552006d9b", "kind": "user", "text": "QA chronology request" },
                    { "id": "activity-5b7551e1ee834ad98d95274b382b4ef4", "kind": "activity", "status": "Completed" },
                    { "id": "88b852c448e7447d854bb511ed8c9529", "kind": "assistant", "text": "QA PREAMBLE ONE\n" },
                    { "id": "activity-84d546e82ecb4547be627ab037baf9ab", "kind": "activity", "status": "Completed", "actionCount": 1, "durationMs": 27290.1523 },
                    { "id": "7341167d7b874750aec8e2b7bc1093ec", "kind": "assistant", "text": "QA PREAMBLE TWO\n" },
                    { "id": "activity-0dc20c48e23245fc868ed43c7eca2c0c", "kind": "activity", "status": "Completed", "actionCount": 1, "durationMs": 25371.5383 },
                    { "id": "43a1dc625cd643d689fd7646ac2d1ce2", "kind": "assistant", "text": "QA FINAL ANSWER" }
                  ]
                },
                {
                  "id": "turn-1",
                  "items": [
                    { "id": "c2e74e6460f14e10a2043ff5a4967d12", "kind": "user", "text": "Second QA request" },
                    { "id": "664200aff58f41a7b50faf526b0f016a", "kind": "error", "text": "Error: Session is already tracked by this client." }
                  ]
                }
              ]
            }
            """, RemoteJsonContext.Default.RemoteTranscript)!;
        transcript.ChatId = chatId;
        transcript.Status.ChatId = chatId;
        return transcript;
    }

    private sealed class TimelineSink : IRemoteCommandSink, IRemoteActivityDetailSink
    {
        public int DetailRequests { get; private set; }
        public string? LastActivityId { get; private set; }
        public Task<RemoteCommandResult> SendCommandAsync(RemoteCommand command) =>
            Task.FromResult(new RemoteCommandResult { Ok = true });
        public Task<RemoteUploadResponse> UploadAsync(string fileName, ReadOnlyMemory<byte> content) =>
            Task.FromResult(new RemoteUploadResponse { Ok = true });
        public Task<RemoteActivityDetails?> GetActivityDetailsAsync(Guid chatId, string activityId)
        {
            DetailRequests++;
            LastActivityId = activityId;
            return Task.FromResult<RemoteActivityDetails?>(new RemoteActivityDetails
            {
                ChatId = chatId, ActivityId = activityId
            });
        }
    }
}
