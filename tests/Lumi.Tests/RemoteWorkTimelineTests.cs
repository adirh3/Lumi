using System.Text.Json;
using Lumi.Models;
using Lumi.Mobile.ViewModels;
using Lumi.Remote.Protocol;
using Lumi.Services;
using Lumi.Services.Remote;
using Xunit;

namespace Lumi.Tests;

public sealed class RemoteWorkTimelineTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 9, 7, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LateCompletedFileNotificationKeepsWorkCollapsedAndFilesAccessible()
    {
        var messages = Timeline();
        var completed = Assert.Single(Build(messages).Turns);
        using var displayed = new TranscriptTurnViewModel(completed.Id);
        displayed.Apply(completed);
        var work = Assert.Single(displayed.DisplayItems.OfType<WorkSummaryItemViewModel>());

        var path = Path.Combine(Path.GetTempPath(), "late-workspace-file.cs");
        messages.Add(Message("tool", JsonSerializer.Serialize(new { filePath = path, operation = "Modify" }),
            100, ToolDisplayHelper.WorkspaceFileChangedToolName));
        var updated = Assert.Single(Build(messages).Turns);

        Assert.Equal(completed.FinalAnswerId, updated.FinalAnswerId);
        Assert.Equal(completed.WorkDurationMs, updated.WorkDurationMs);
        displayed.Apply(updated);
        Assert.Same(work, Assert.Single(displayed.DisplayItems.OfType<WorkSummaryItemViewModel>()));
        Assert.False(work.IsExpanded);
        var final = Assert.Single(displayed.DisplayItems.OfType<AssistantItemViewModel>());
        Assert.Equal("Final answer", final.Text);
        var notification = Assert.IsType<ActivitySummaryItemViewModel>(displayed.DisplayItems[^1]);
        Assert.True(notification.HasFileChanges);
        Assert.Contains(updated.Items[^1].FileChanges!, file =>
            file.FileName == Path.GetFileName(path) && file.Operation == "Modified");
    }

    [Theory]
    [InlineData("workspace_file_changed", "InProgress")]
    [InlineData("workspace_file_changed", "Stopped")]
    [InlineData("powershell", "Completed")]
    public void PostAnswerWorkStillPreventsCompletion(string toolName, string status)
    {
        var messages = Timeline();
        var message = Message("tool", "{}", 100, toolName);
        message.ToolStatus = status;
        messages.Add(message);
        Assert.Null(Assert.Single(Build(messages).Turns).FinalAnswerId);
    }

    [Fact]
    public void RecordedQaCompletedTurnKeepsItsFinalAndTimingDespiteTheNextUserTurnError()
    {
        // The isolated QA chat 8a9abc0c-67cb-498c-8ac1-1003220be758, read on 2026-09-07.
        // Only display text is reduced; identities, chronology and persisted timing are retained.
        var messages = new List<ChatMessage>
        {
            Recorded("17f3a3d7-7951-4c9f-91b0-c93552006d9b", "user", "QA chronology request", "09:55:22.7238722"),
            Recorded("5b7551e1-ee83-4ad9-8d95-274b382b4ef4", "reasoning", "QA reasoning", "09:55:35.1654092"),
            Recorded("88b852c4-48e7-447d-854b-b511ed8c9529", "assistant", "QA PREAMBLE ONE\n", "09:55:35.5969368"),
            Recorded("84d546e8-2ecb-4547-be62-7ab037baf9ab", "tool", "{}", "09:55:36.1825050"),
            Recorded("7341167d-7b87-4750-aec8-e2b7bc1093ec", "assistant", "QA PREAMBLE TWO\n", "09:56:05.2146535"),
            Recorded("0dc20c48-e232-45fc-868e-d43c7eca2c0c", "tool", "{}", "09:56:06.3379233"),
            Recorded("43a1dc62-5cd6-43d6-89fd-7646ac2d1ce2", "assistant", "QA FINAL ANSWER", "09:56:33.4269647"),
            Recorded("c2e74e64-60f1-4e10-a204-3ff5a4967d12", "user", "Second QA request", "10:02:10.3157461"),
            Recorded("664200af-f58f-41a7-b50f-af526b0f016a", "error", "Error: Session is already tracked by this client.", "10:02:11.9448800")
        };
        messages[3].ToolStartedAt = DateTimeOffset.Parse("2026-09-07T06:55:36.1774508+00:00");
        messages[3].ToolDurationMs = 27_290.1523;
        messages[5].ToolStartedAt = DateTimeOffset.Parse("2026-09-07T06:56:06.3377395+00:00");
        messages[5].ToolDurationMs = 25_371.5383;

        var transcript = Build(messages);
        Assert.False(transcript.Status.IsBusy);
        Assert.Equal(2, transcript.Turns.Count);
        Assert.Equal("43a1dc625cd643d689fd7646ac2d1ce2", transcript.Turns[0].FinalAnswerId);
        Assert.Equal(58_261.5555, transcript.Turns[0].WorkDurationMs!.Value, 4);
        Assert.Equal(new[] { "user", "activity", "assistant", "activity", "assistant", "activity", "assistant" },
            transcript.Turns[0].Items.Select(item => item.Kind));
        Assert.Null(transcript.Turns[1].FinalAnswerId);
        Assert.Equal(new[] { "user", "error" }, transcript.Turns[1].Items.Select(item => item.Kind));

        static ChatMessage Recorded(string id, string role, string content, string time) => new()
        {
            Id = Guid.Parse(id), Role = role, Content = content,
            Timestamp = DateTimeOffset.Parse($"2026-09-07T{time}+03:00"),
            ToolName = role == "tool" ? "powershell" : null,
            ToolStatus = role == "tool" ? "Completed" : null
        };
    }

    [Fact]
    public void ThinkingRecordedAfterTheAnswerFoldsWithTheWorkItEnded()
    {
        var messages = Timeline();
        messages.Add(Message("reasoning", "the answer's own thinking, recorded late", 93));

        var turn = Assert.Single(Build(messages).Turns);
        Assert.Equal(messages[^2].Id.ToString("N"), turn.FinalAnswerId);
        Assert.True(turn.IsSettled);
        Assert.Equal("activity", turn.Items[^1].Kind);

        using var displayed = new TranscriptTurnViewModel(turn.Id);
        displayed.Apply(turn);
        Assert.Collection(displayed.DisplayItems,
            item => Assert.IsType<UserTurnItemViewModel>(item),
            item => Assert.IsType<WorkSummaryItemViewModel>(item),
            item => Assert.Equal("Final answer", Assert.IsType<AssistantItemViewModel>(item).Text));
    }

    [Fact]
    public void SteeredTurnFoldsItsUpdatesUpToItsLastWordWithoutCallingItAnAnswer()
    {
        var messages = new List<ChatMessage>
        {
            Message("user", "Do it", 0),
            Message("assistant", "Checking first", 1),
            Message("tool", "{}", 2, "view"),
            Message("assistant", "Now verifying", 3),
            Message("tool", "{}", 4, "powershell"),
            Message("assistant", "Still verifying", 5),
            Message("tool", "{}", 6, "rg"),
            Message("user", "Also check the light theme", 7),
            Message("assistant", "Done, both themes pass", 9)
        };

        var transcript = Build(messages);
        var steered = transcript.Turns[0];
        Assert.Null(steered.FinalAnswerId);
        Assert.True(steered.IsSettled);

        using var displayed = new TranscriptTurnViewModel(steered.Id);
        displayed.Apply(steered);
        Assert.Collection(displayed.DisplayItems,
            item => Assert.IsType<UserTurnItemViewModel>(item),
            item => Assert.Equal("Worked · 2 actions", Assert.IsType<WorkSummaryItemViewModel>(item).Label),
            item =>
            {
                var last = Assert.IsType<AssistantItemViewModel>(item);
                Assert.Equal("Still verifying", last.Text);
                Assert.False(last.IsFinalAnswer);
            },
            item => Assert.IsType<ActivitySummaryItemViewModel>(item));
    }

    [Fact]
    public void BackgroundWorkStaysInViewWhileTheRestOfTheSettledTurnFolds()
    {
        var messages = Timeline();
        messages[^2].ToolCallId = "background-tool";
        var transcript = RemoteProjector.BuildTranscript(
            new Chat(), messages, new RemoteChatStatus(),
            showReasoning: true, showToolCalls: true, revision: 1, compact: true,
            runningBackgroundToolCallIds: new HashSet<string> { "background-tool" });
        var turn = Assert.Single(transcript.Turns);
        Assert.Null(turn.FinalAnswerId);
        Assert.True(turn.IsSettled);

        using var displayed = new TranscriptTurnViewModel(turn.Id);
        displayed.Apply(turn);
        Assert.Collection(displayed.DisplayItems,
            item => Assert.IsType<UserTurnItemViewModel>(item),
            item => Assert.IsType<WorkSummaryItemViewModel>(item),
            item => Assert.True(Assert.IsType<ActivitySummaryItemViewModel>(item).IsRunning),
            item => Assert.Equal("Final answer", Assert.IsType<AssistantItemViewModel>(item).Text));
    }

    [Fact]
    public void LiveTurnIsNeverSettledAndKeepsEveryUpdateInView()
    {
        var messages = Timeline();
        var turn = Assert.Single(Build(messages, busy: true).Turns);
        Assert.Null(turn.IsSettled);
        Assert.Null(turn.FinalAnswerId);

        using var displayed = new TranscriptTurnViewModel(turn.Id);
        displayed.Apply(turn);
        Assert.DoesNotContain(displayed.DisplayItems, item => item is WorkSummaryItemViewModel);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnUpdateTheSdkRecordedAsProgressIsNotTheTurnsAnswer(bool fromLiveCapture)
    {
        var messages = Timeline();
        messages[^1].IsProgress = true;

        var turn = Assert.Single(Build(fromLiveCapture
            ? messages.Select(LumiRemoteServer.CloneMessageForRemote).ToList()
            : messages).Turns);
        Assert.Null(turn.FinalAnswerId);
        Assert.True(turn.IsSettled);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void RecordedAnswerPartsStayVisibleAndOnlyProgressFolds(bool hasWork, bool fromLiveCapture)
    {
        var messages = hasWork ? Timeline() : [Message("user", "Do it", 0)];
        if (hasWork)
        {
            messages[1].IsProgress = true;
            messages[4].IsProgress = true;
        }
        else
            messages.Add(Message("assistant", "Final answer", 92));
        messages[^1].IsProgress = false;
        messages.Add(Message("assistant", "Second answer part", 94));
        messages[^1].IsProgress = false;

        var transcript = Build(fromLiveCapture
            ? messages.Select(LumiRemoteServer.CloneMessageForRemote).ToList()
            : messages);
        var restored = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(transcript, RemoteJsonContext.Default.RemoteTranscript),
            RemoteJsonContext.Default.RemoteTranscript)!;
        var turn = Assert.Single(restored.Turns);
        using var displayed = new TranscriptTurnViewModel(turn.Id);
        displayed.Apply(turn);

        Assert.Equal(new[] { "Final answer", "Second answer part" },
            displayed.DisplayItems.OfType<AssistantItemViewModel>().Select(item => item.Text));
        Assert.Equal(hasWork ? 1 : 0, displayed.DisplayItems.OfType<WorkSummaryItemViewModel>().Count());
        if (hasWork)
        {
            var work = Assert.Single(displayed.DisplayItems.OfType<WorkSummaryItemViewModel>());
            work.ToggleCommand.Execute(null);
            Assert.Equal(new[] { "Checking first", "Now verifying" },
                work.Items.OfType<AssistantItemViewModel>().Select(item => item.Text));
        }
    }

    [Fact]
    public void CompactHistoryCountsEveryRecordedAnswerPart()
    {
        var messages = new List<ChatMessage>();
        for (var turn = 0; turn < 2; turn++)
        {
            messages.Add(Message("user", $"Question {turn}", turn * 10));
            for (var part = 0; part < 3; part++)
            {
                var answer = Message("assistant", $"Answer {turn}, part {part}", turn * 10 + part + 1);
                answer.IsProgress = false;
                messages.Add(answer);
            }
        }

        var window = RemoteProjector.SelectCompactTranscriptWindow(messages, null, maxVisibleItems: 6);

        Assert.Equal(4, window.StartMessageIndex);
        Assert.Equal(messages.Count, window.EndMessageIndex);
        Assert.True(window.HasEarlierMessages);
    }

    [Fact]
    public void ActiveCompactWorkStaysBetweenAssistantMessagesAndDetailsAreSegmentScoped()
    {
        var messages = Timeline();
        messages[^1].IsStreaming = true;
        var transcript = Build(messages, busy: true);
        var turn = Assert.Single(transcript.Turns);

        Assert.Equal(
            new[] { "user", "assistant", "activity", "assistant", "activity", "assistant" },
            turn.Items.Select(item => item.Kind));
        Assert.Equal("Checking first", turn.Items[1].Text);
        Assert.Equal("Now verifying", turn.Items[3].Text);
        Assert.True(turn.Items[^1].IsStreaming);
        Assert.Null(turn.FinalAnswerId);
        var activities = turn.Items.Where(item => item.Kind == "activity").ToArray();
        Assert.Equal(2, activities.Length);
        Assert.All(activities, item =>
        {
            Assert.Equal(1, item.ActionCount);
            Assert.Null(item.Tools);
        });

        var firstDetails = RemoteProjector.BuildActivityDetails(
            new Chat(), messages, activities[0].ActivityId!);
        var secondDetails = RemoteProjector.BuildActivityDetails(
            new Chat(), messages, activities[1].ActivityId!);
        Assert.Equal("view", Assert.Single(firstDetails!.Tools).Name);
        Assert.Equal("powershell", Assert.Single(secondDetails!.Tools).Name);
        var wire = JsonSerializer.Serialize(transcript, RemoteJsonContext.Default.RemoteTranscript);
        Assert.DoesNotContain("private reasoning", wire);
        Assert.DoesNotContain("hidden output", wire);
    }

    [Fact]
    public void CompletionAndHistoricalReconnectUsePersistedWorkTimeNotTheCurrentClockOrTailStatus()
    {
        var messages = Timeline();
        var completed = Assert.Single(Build(messages).Turns);
        Assert.Equal(messages[^1].Id.ToString("N"), completed.FinalAnswerId);
        Assert.Equal(91_000d, completed.WorkDurationMs);
        var completedIds = completed.Items.Select(item => item.Id).ToArray();

        var before = messages.Count;
        messages.Add(Message("user", "Tomorrow's question", 86_400));
        messages.Add(Message("assistant", "Starting another turn", 86_401));
        var historical = Build(messages, busy: true, before: before);
        var previousTurn = Assert.Single(historical.Turns);
        Assert.False(historical.IsLatestWindow);
        Assert.Equal(completed.FinalAnswerId, previousTurn.FinalAnswerId);
        Assert.Equal(completed.WorkDurationMs, previousTurn.WorkDurationMs);
        Assert.Equal(completedIds, previousTurn.Items.Select(item => item.Id));
        Assert.Equal(before, historical.WindowEndMessageIndex);
    }

    [Fact]
    public void WorkDurationIncludesPreambleAndReasoningWithoutAddingParallelToolDurations()
    {
        var first = Message("tool", "{}", 1, "view");
        var second = Message("tool", "{}", 1, "view");
        first.ToolStartedAt = second.ToolStartedAt = StartedAt.AddSeconds(1);
        first.ToolDurationMs = second.ToolDurationMs = 10_000;
        var messages = new List<ChatMessage>
        {
            Message("user", "Work", -1),
            Message("assistant", "Preamble", 0),
            first,
            second,
            Message("assistant", "Final", 12)
        };

        var turn = Assert.Single(Build(messages).Turns);
        Assert.Equal(12_000d, turn.WorkDurationMs);
        Assert.Equal(20_000d, Assert.Single(turn.Items, item => item.Kind == "activity").DurationMs);
    }

    [Theory]
    [InlineData("Stopped", false)]
    [InlineData("Stopped", true)]
    [InlineData("Failed", false)]
    public void InterruptedWorkDoesNotMistakePreambleOrCanceledPartialTextForFinal(
        string toolStatus, bool partialAnswer)
    {
        var messages = Timeline();
        messages.RemoveAt(messages.Count - 1);
        messages[^1].ToolStatus = toolStatus;
        if (partialAnswer)
            messages.Add(Message("assistant", "Partial answer before cancellation", 30));
        else
            messages.Add(Message("error", "Turn interrupted", 30));

        var turn = Assert.Single(Build(messages).Turns);
        Assert.Null(turn.FinalAnswerId);
        Assert.Null(turn.WorkDurationMs);
        Assert.Contains(turn.Items, item => item.Text == "Checking first");
        Assert.Contains(turn.Items, item => item.Status == toolStatus);
    }

    [Fact]
    public void StreamingAndAuthoritativeBackgroundWorkPreventPrematureCompletion()
    {
        var messages = Timeline();
        messages[^1].IsStreaming = true;
        Assert.Null(Assert.Single(Build(messages).Turns).FinalAnswerId);

        messages[^1].IsStreaming = false;
        messages[^2].ToolCallId = "background-tool";
        var transcript = RemoteProjector.BuildTranscript(
            new Chat(), messages, new RemoteChatStatus(),
            showReasoning: true, showToolCalls: true, revision: 1, compact: true,
            runningBackgroundToolCallIds: new HashSet<string> { "background-tool" });
        Assert.Null(Assert.Single(transcript.Turns).FinalAnswerId);
    }

    [Fact]
    public void LegacyPartialPageCannotPromoteAnInterimWhenFinalAnswerIsOutsideItsWindow()
    {
        var messages = Timeline();
        var page = new TranscriptMessageWindow(messages.Take(5).ToArray(), 0, 5, messages.Count);
        var transcript = RemoteProjector.BuildTranscript(
            new Chat(), page, new RemoteChatStatus(),
            showReasoning: true, showToolCalls: true, revision: 1, compact: true,
            activitySourceMessages: messages);

        Assert.Null(Assert.Single(transcript.Turns).FinalAnswerId);
    }

    [Fact]
    public void DefaultHistoryCountsCollapsedWorkOnceAndIncludesRecentConversation()
    {
        var messages = DenseHistory();
        var prompts = messages.Where(message => message.Role == "user").ToArray();

        var limit = LumiRemoteServer.ResolveTranscriptWindowLimit(null, null, compact: true);
        var latest = RemoteProjector.SelectCompactTranscriptWindow(messages, null, limit);
        var transcript = RemoteProjector.BuildTranscript(
            new Chat(), latest, new RemoteChatStatus(), true, true, 1,
            compact: true, activitySourceMessages: messages);

        Assert.Equal(16, transcript.Turns.Count);
        Assert.Equal(messages.IndexOf(prompts[4]), latest.StartMessageIndex);
        Assert.Equal(messages.Count, latest.EndMessageIndex);
        Assert.True(latest.HasEarlierMessages);
        Assert.True(latest.IsLatestWindow);
        Assert.Equal(
            prompts.Skip(4).Select(prompt => prompt.Id.ToString("N")),
            transcript.Turns.Select(turn => turn.Items[0].Id));
        foreach (var remoteTurn in transcript.Turns)
        {
            using var displayed = new TranscriptTurnViewModel(remoteTurn.Id);
            displayed.Apply(remoteTurn);
            Assert.Equal(3, displayed.DisplayItems.Count);
            var work = Assert.IsType<WorkSummaryItemViewModel>(displayed.DisplayItems[1]);
            Assert.False(work.IsExpanded);
            Assert.IsType<AssistantItemViewModel>(displayed.DisplayItems[^1]);
            work.ToggleCommand.Execute(null);
            Assert.Equal(60, work.Items.Count);
        }
        Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(
            transcript, RemoteJsonContext.Default.RemoteTranscript).Length,
            1, RemoteProtocol.MobileTranscriptJsonByteLimit);

        var earlier = RemoteProjector.SelectCompactTranscriptWindow(
            messages, latest.StartMessageIndex, RemoteProtocol.CompactTranscriptWindowVisibleItemLimit);
        Assert.Equal(0, earlier.StartMessageIndex);
        Assert.Equal(latest.StartMessageIndex, earlier.EndMessageIndex);
        Assert.False(earlier.HasEarlierMessages);
        Assert.True(earlier.HasLaterMessages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LiveStatusKeepsUnsettledRowsInTheInitialHistoryBudget(bool streaming)
    {
        var messages = new List<ChatMessage>
        {
            Message("user", "Earlier prompt", 0),
            Message("assistant", "Earlier answer", 1),
            Message("user", "Current prompt", 2)
        };
        for (var step = 0; step < 30; step++)
        {
            messages.Add(Message("assistant", $"Checking step {step}", step * 2 + 3));
            messages.Add(Message("tool", "{}", step * 2 + 4, "view"));
        }
        messages.Add(Message("assistant", "Not settled yet", 90));
        var status = new RemoteChatStatus { IsBusy = !streaming, IsStreaming = streaming };
        var window = RemoteProjector.SelectCompactTranscriptWindow(
            messages, null, RemoteProtocol.InitialCompactTranscriptWindowVisibleItemLimit,
            latestStatus: status);
        var transcript = RemoteProjector.BuildTranscript(
            new Chat(), window, status, true, true, 1,
            compact: true, activitySourceMessages: messages);

        Assert.Equal(2, window.StartMessageIndex);
        var turn = Assert.Single(transcript.Turns);
        Assert.Null(turn.FinalAnswerId);
        using var displayed = new TranscriptTurnViewModel(turn.Id);
        displayed.Apply(turn);
        Assert.Equal(62, displayed.DisplayItems.Count);
        Assert.DoesNotContain(displayed.DisplayItems, item => item is WorkSummaryItemViewModel);
    }

    [Fact]
    public void CollapsedWorkStillCountsTowardTheSourceTextBudget()
    {
        var messages = new List<ChatMessage>
        {
            Message("user", "Earlier prompt", 0),
            Message("assistant", "Earlier answer", 1),
            Message("user", "Latest prompt", 2),
            Message("assistant", new string('x', 256), 3),
            Message("tool", "{}", 4, "view"),
            Message("assistant", "Final answer", 5)
        };
        var window = RemoteProjector.SelectCompactTranscriptWindow(
            messages, null, RemoteProtocol.InitialCompactTranscriptWindowVisibleItemLimit,
            textBudgetCharacters: 128);

        Assert.Equal(2, window.StartMessageIndex);
        Assert.Equal(messages.Count, window.EndMessageIndex);
        Assert.True(window.HasEarlierMessages);
        Assert.Contains(window.Messages, message => message.Content == new string('x', 256));
    }

    [Fact]
    public void OlderDenseHistoryPagesStayReadableAndKeepGapFreeCursors()
    {
        var messages = DenseHistory(turnCount: 200);
        var latest = RemoteProjector.SelectCompactTranscriptWindow(
            messages, null, RemoteProtocol.InitialCompactTranscriptWindowVisibleItemLimit);
        var before = latest.StartMessageIndex;
        while (before > 0)
        {
            var window = RemoteProjector.SelectCompactTranscriptWindow(
                messages, before, RemoteProtocol.CompactTranscriptWindowVisibleItemLimit);
            var transcript = RemoteProjector.BuildTranscript(
                new Chat(), window, new RemoteChatStatus(), true, true, 1,
                compact: true, activitySourceMessages: messages);

            Assert.DoesNotContain(transcript.Turns, turn => turn.Id == "mobile-wire-limit");
            Assert.Equal(before, transcript.WindowEndMessageIndex);
            Assert.InRange(transcript.WindowStartMessageIndex, 0, before - 1);
            Assert.Equal(transcript.WindowStartMessageIndex > 0, transcript.HasEarlierMessages);
            var firstUser = transcript.Turns[0].Items[0];
            Assert.Equal(RemoteProtocol.ItemKinds.User, firstUser.Kind);
            Assert.Equal(messages[transcript.WindowStartMessageIndex].Id.ToString("N"), firstUser.Id);
            Assert.Equal(
                messages.Skip(transcript.WindowStartMessageIndex)
                    .Take(before - transcript.WindowStartMessageIndex)
                    .Where(message => message.Role == "user")
                    .Select(message => message.Id.ToString("N")),
                transcript.Turns.Select(turn => turn.Items[0].Id));
            Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(
                transcript, RemoteJsonContext.Default.RemoteTranscript).Length,
                1, RemoteProtocol.MobileTranscriptJsonByteLimit);

            before = transcript.WindowStartMessageIndex;
        }
    }

    [Fact]
    public void DenseSegmentsKeepBoundedAnchorsAndWholeTurnPaging()
    {
        var messages = new List<ChatMessage>();
        for (var turnIndex = 0; turnIndex < 2; turnIndex++)
        {
            messages.Add(Message("user", $"Prompt {turnIndex}", turnIndex * 100));
            messages.Add(Message("assistant", "Preamble", turnIndex * 100 + 1));
            for (var index = 0; index < 300; index++)
                messages.Add(Message("tool", new string('x', 10_000), 2, "view"));
            messages.Add(Message("assistant", "Interim", turnIndex * 100 + 10));
            for (var index = 0; index < 300; index++)
                messages.Add(Message("tool", new string('x', 10_000), 12, "powershell"));
            messages.Add(Message("assistant", "Final", turnIndex * 100 + 20));
        }

        var latest = RemoteProjector.SelectCompactTranscriptWindow(messages, null, maxVisibleItems: 3);
        Assert.Equal(6, latest.Messages.Count);
        Assert.True(latest.HasEarlierMessages);
        var earlier = RemoteProjector.SelectCompactTranscriptWindow(
            messages, latest.StartMessageIndex, maxVisibleItems: 3);
        Assert.Equal(0, earlier.StartMessageIndex);
        Assert.Equal(latest.StartMessageIndex, earlier.EndMessageIndex);
        var transcript = RemoteProjector.BuildTranscript(
            new Chat(), latest, new RemoteChatStatus(), true, true, 1,
            compact: true, activitySourceMessages: messages);
        var turn = Assert.Single(transcript.Turns);
        Assert.Equal(messages[^1].Id.ToString("N"), turn.FinalAnswerId);
        Assert.All(turn.Items.Where(item => item.Kind == "activity"),
            activity => Assert.Equal(300, activity.ActionCount));
        Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(
            transcript, RemoteJsonContext.Default.RemoteTranscript).Length,
            1, RemoteProtocol.MobileTranscriptJsonByteLimit);
    }

    internal static List<ChatMessage> DenseHistory(int turnCount = 20)
    {
        var messages = new List<ChatMessage>();
        for (var turnIndex = 0; turnIndex < turnCount; turnIndex++)
        {
            messages.Add(Message("user", $"Prompt {turnIndex}", turnIndex * 100));
            for (var step = 0; step < 30; step++)
            {
                messages.Add(Message("assistant", $"Checking step {step}", turnIndex * 100 + step * 2 + 1));
                messages.Add(Message("tool", "{}", turnIndex * 100 + step * 2 + 2, "view"));
            }
            messages.Add(Message("assistant", $"Answer {turnIndex}", turnIndex * 100 + 90));
        }
        return messages;
    }

    private static List<ChatMessage> Timeline() =>
    [
        Message("user", "Do it", 0),
        Message("assistant", "Checking first", 1),
        Message("reasoning", "private reasoning", 2),
        Message("tool", "{}", 3, "view"),
        Message("assistant", "Now verifying", 10),
        Message("tool", "{}", 12, "powershell"),
        Message("assistant", "Final answer", 92)
    ];

    private static ChatMessage Message(string role, string text, int second, string? tool = null) => new()
    {
        Role = role,
        Content = text,
        Timestamp = StartedAt.AddSeconds(second),
        ToolName = tool,
        ToolStatus = tool is null ? null : "Completed",
        ToolOutput = tool is null ? null : "hidden output"
    };

    private static RemoteTranscript Build(List<ChatMessage> messages, bool busy = false, int? before = null)
    {
        var window = RemoteProjector.SelectCompactTranscriptWindow(messages, before, maxVisibleItems: 40);
        return RemoteProjector.BuildTranscript(
            new Chat(), window, new RemoteChatStatus { IsBusy = busy }, true, true, 1,
            compact: true, activitySourceMessages: messages);
    }
}
