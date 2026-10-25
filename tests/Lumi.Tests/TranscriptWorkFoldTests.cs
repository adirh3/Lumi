using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Threading.Tasks;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// Progress updates — the narration an assistant writes while it works — stay in view while it works,
/// and once the answer finishes or the turn ends they fold with the work into one "Worked for" row.
/// </summary>
public sealed class TranscriptWorkFoldTests
{
    private static readonly DateTimeOffset AskedAt = new(2026, 10, 14, 18, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("commentary", false, true)]
    [InlineData("commentary", true, true)]
    [InlineData("final_answer", true, false)]
    [InlineData("final_answer", false, false)]
    [InlineData(null, true, true)]
    [InlineData(null, false, false)]
    [InlineData("thinking", true, true)]
    public void AssistantProgress_TrustsThePhaseAndOtherwiseTheToolRequests(string? phase, bool requestedTools, bool expected)
        => Assert.Equal(expected, AssistantProgress.IsProgress(phase, requestedTools));

    [Fact]
    public void AssistantProgress_ToolRequestsDemoteEarlierPiecesOfTheSameModelCall()
    {
        var earlierCall = Assistant("Earlier reply", apiCallId: "call-1");
        var firstPiece = Assistant("Let me look at the logs.", apiCallId: "call-2");
        var messages = new List<ChatMessage>
        {
            new() { Role = "user", Content = "Why is the build red?" },
            earlierCall,
            new() { Role = "reasoning", Content = "Thinking" },
            firstPiece,
            new() { Role = "reasoning", Content = "More thinking" },
        };

        var demoted = AssistantProgress.DemoteEarlierPiecesOfCall(messages, phase: null, requestedTools: true, apiCallId: "call-2");

        Assert.Same(firstPiece, Assert.Single(demoted));
        Assert.True(firstPiece.IsProgress);
        Assert.Null(earlierCall.IsProgress);
    }

    [Fact]
    public void AssistantProgress_PhasedOrToolFreePiecesDemoteNothing()
    {
        var piece = Assistant("Part of the answer", apiCallId: "call-1");
        List<ChatMessage> messages = [piece];

        Assert.Empty(AssistantProgress.DemoteEarlierPiecesOfCall(messages, "commentary", true, "call-1"));
        Assert.Empty(AssistantProgress.DemoteEarlierPiecesOfCall(messages, null, false, "call-1"));
        Assert.Empty(AssistantProgress.DemoteEarlierPiecesOfCall(messages, null, true, null));
        Assert.Null(piece.IsProgress);
    }

    [Fact]
    public void Recovery_ClassifiesRecoveredMessagesAndTheEarlierPiecesOfASplitReply()
    {
        var lines = new[]
        {
            "{\"type\":\"user.message\",\"data\":{\"content\":\"continue\"}}",
            "{\"type\":\"assistant.message\",\"data\":{\"content\":\"I'll check the tests.\",\"apiCallId\":\"call-1\"}}",
            "{\"type\":\"assistant.message\",\"data\":{\"content\":\"\",\"apiCallId\":\"call-1\",\"toolRequests\":[{\"toolCallId\":\"tool-1\"}]}}",
            "{\"type\":\"tool.execution_start\",\"data\":{\"toolCallId\":\"tool-1\",\"toolName\":\"view\"}}",
            "{\"type\":\"tool.execution_complete\",\"data\":{\"toolCallId\":\"tool-1\",\"success\":true}}",
            "{\"type\":\"assistant.message\",\"data\":{\"content\":\"Commentary\",\"phase\":\"commentary\"}}",
            "{\"type\":\"assistant.message\",\"data\":{\"content\":\"All tests pass.\",\"phase\":\"final_answer\"}}",
        };

        var analysis = PendingTurnRecoveryAnalyzer.AnalyzePersistedLog(lines, expectedSessionUserMessageCount: 1);

        Assert.Collection(analysis.AssistantMessages,
            message => Assert.Equal(("I'll check the tests.", (bool?)true), (message.Content, message.IsProgress)),
            message => Assert.Equal(("Commentary", (bool?)true), (message.Content, message.IsProgress)),
            message => Assert.Equal(("All tests pass.", (bool?)false), (message.Content, message.IsProgress)));
    }

    [Fact]
    public void IsProgress_RoundTripsThroughChatPersistenceAndClone()
    {
        var json = JsonSerializer.Serialize(
            new List<ChatMessage>
            {
                new() { Role = "assistant", Content = "Checking the build.", IsProgress = true },
                new() { Role = "assistant", Content = "Recorded before Lumi kept roles." },
            },
            AppDataJsonContext.Default.ListChatMessage);

        var restored = JsonSerializer.Deserialize(json, AppDataJsonContext.Default.ListChatMessage)!;

        Assert.True(restored[0].IsProgress);
        Assert.True(restored[0].Clone().IsProgress);
        Assert.Null(restored[1].IsProgress);
    }

    [Fact]
    public void LiveTurn_FoldsUpdatesAndTheirWorkIntoOneWorkedRowWhenTheAnswerFinishes()
    {
        Lumi.Localization.Loc.Load("en");
        var (builder, turns) = CreateLiveBuilder();
        builder.ProcessMessageToTranscript(User("Fix the build", AskedAt));

        var firstUpdate = StreamAssistant(builder, "I'll check the build output.", isProgress: true, AskedAt.AddSeconds(2));
        builder.ProcessMessageToTranscript(Tool("tool-1", "Completed"));
        var secondUpdate = StreamAssistant(builder, "Found it: a missing using.", isProgress: true, AskedAt.AddSeconds(40));
        builder.ProcessMessageToTranscript(Tool("tool-2", "Completed"));
        var answer = StreamAssistant(builder, "Fixed: added the missing using.", isProgress: false, AskedAt.AddSeconds(102));
        builder.CollapseCompletedBlocksInCurrentTurn();

        var work = turns[^1];
        Assert.Equal(2, work.Items.Count);
        var history = Assert.IsType<TurnSummaryItem>(work.Items[0]);
        Assert.Equal("Worked for 1m 42s", history.Label);
        Assert.False(history.IsExpanded);
        Assert.Collection(history.InnerItems,
            item => Assert.Equal(firstUpdate.Message.Id, Assert.IsType<AssistantMessageItem>(item).MessageId),
            item => Assert.IsType<SingleToolItem>(item),
            item => Assert.Equal(secondUpdate.Message.Id, Assert.IsType<AssistantMessageItem>(item).MessageId),
            item => Assert.IsType<SingleToolItem>(item));
        Assert.All(history.InnerItems.OfType<AssistantMessageItem>(), static item => Assert.True(item.IsProgress));
        var shown = Assert.IsType<AssistantMessageItem>(work.Items[1]);
        Assert.Equal(answer.Message.Id, shown.MessageId);
        Assert.False(shown.IsProgress);
    }

    [Fact]
    public void LiveTurn_LateReasoningPrecedesTheCompletedAnswer()
    {
        var (builder, turns) = CreateLiveBuilder();
        builder.ProcessMessageToTranscript(User("Explain the result", AskedAt));
        var answer = StreamAssistant(builder, "The result is ready.", isProgress: false, AskedAt.AddSeconds(10));
        var reasoning = Reasoning("Checking how to explain the result.");
        builder.ProcessMessageToTranscript(reasoning);
        builder.AppendModelLabel("gpt-6.1-sol");

        builder.CollapseCompletedBlocksInCurrentTurn();
        builder.CollapseCompletedBlocksInCurrentTurn();

        Assert.Collection(turns[^1].Items.Where(static item => item is not TurnModelItem),
            item => Assert.Equal(reasoning.Message.Id, Assert.IsType<ReasoningItem>(item).MessageId),
            item => Assert.Equal(answer.Message.Id, Assert.IsType<AssistantMessageItem>(item).MessageId));
    }

    [Fact]
    public void LateReasoning_DoesNotCaptureReasoningWhenTheSessionResumes()
    {
        var (builder, turns) = CreateLiveBuilder();
        builder.ProcessMessageToTranscript(User("Check the background work", AskedAt));
        StreamAssistant(builder, "The background work is running.", isProgress: false, AskedAt.AddSeconds(5));
        builder.AppendModelLabel("gpt-6.1-sol");
        builder.CollapseCompletedBlocksInCurrentTurn();

        var answer = StreamAssistant(builder, "The first result is ready.", isProgress: false, AskedAt.AddSeconds(10));
        var lateReasoning = Reasoning("Explain the first result.");
        builder.ProcessMessageToTranscript(lateReasoning);
        builder.AppendModelLabel("gpt-6.1-sol");
        builder.CollapseCompletedBlocksInCurrentTurn();

        var nextReasoning = Reasoning("Inspect the next result.", isStreaming: true);
        builder.ProcessMessageToTranscript(nextReasoning);

        var work = turns[^1];
        var latest = Assert.IsType<ReasoningItem>(work.Items[^1]);
        Assert.Equal("Inspect the next result.", latest.Content);
        Assert.True(latest.IsActive);
        Assert.True(work.Items.IndexOf(latest) >
            work.Items.IndexOf(work.Items.OfType<AssistantMessageItem>().Single(item => item.MessageId == answer.Message.Id)));
        var earlier = work.EnumerateItems().OfType<ReasoningItem>()
            .Single(item => item.MessageId == lateReasoning.Message.Id);
        Assert.Equal("Explain the first result.", earlier.Content);
        Assert.False(earlier.IsActive);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(null, true)]
    [InlineData(false, false)]
    [InlineData(null, false)]
    public void Rebuild_LateReasoningPrecedesTheAnswerWithoutRewritingMessages(bool? recordedProgress, bool foldProgressMessages)
    {
        var answer = new ChatMessageViewModel(new ChatMessage
        {
            Role = "assistant",
            Content = "The result is ready.",
            IsProgress = recordedProgress,
            Model = "gpt-6.1-sol",
            Timestamp = AskedAt.AddSeconds(10),
        });
        var reasoning = Reasoning("Checking how to explain the result.");
        var messages = new[] { User("Explain the result", AskedAt), answer, reasoning };

        var turns = CreateBuilder(foldProgressMessages).Rebuild(messages);

        Assert.Collection(turns[^1].Items.Where(static item => item is not TurnModelItem),
            item => Assert.Equal(reasoning.Message.Id, Assert.IsType<ReasoningItem>(item).MessageId),
            item => Assert.Equal(answer.Message.Id, Assert.IsType<AssistantMessageItem>(item).MessageId));
        Assert.Equal(["user", "assistant", "reasoning"], messages.Select(static message => message.Role));
        Assert.Equal(recordedProgress, answer.Message.IsProgress);
        Assert.Equal(AskedAt.AddSeconds(10), answer.Message.Timestamp);
        Assert.Equal("Checking how to explain the result.", reasoning.Message.Content);
    }

    [Fact]
    public void LateReasoning_FoldsWithEarlierWorkBeforeTheAnswer()
    {
        var (builder, turns) = CreateLiveBuilder();
        builder.ProcessMessageToTranscript(User("Check the result", AskedAt));
        builder.ProcessMessageToTranscript(Reasoning("Inspect the result."));
        builder.ProcessMessageToTranscript(Tool("tool-1", "Completed"));
        var answer = StreamAssistant(builder, "The result is correct.", isProgress: false, AskedAt.AddSeconds(10));
        var lateReasoning = Reasoning("Explain the checks.");
        builder.ProcessMessageToTranscript(lateReasoning);

        builder.CollapseCompletedBlocksInCurrentTurn();

        var history = Assert.IsType<TurnSummaryItem>(turns[^1].Items[0]);
        Assert.Collection(history.InnerItems,
            item => Assert.Equal("Inspect the result.", Assert.IsType<ReasoningItem>(item).Content),
            item => Assert.IsType<SingleToolItem>(item),
            item => Assert.Equal(lateReasoning.Message.Id, Assert.IsType<ReasoningItem>(item).MessageId));
        Assert.Equal(answer.Message.Id, Assert.IsType<AssistantMessageItem>(turns[^1].Items[1]).MessageId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReasoningAfterAProgressUpdate_StaysWithTheContinuingWork(bool settle)
    {
        var (builder, turns) = CreateLiveBuilder();
        builder.ProcessMessageToTranscript(User("Check the result", AskedAt));
        var progress = StreamAssistant(builder, "I'll check the result.", isProgress: true, AskedAt.AddSeconds(1));
        var reasoning = Reasoning("Plan the next check.");
        builder.ProcessMessageToTranscript(reasoning);

        if (settle)
            builder.CollapseCompletedBlocksInCurrentTurn();

        Assert.Collection(turns[^1].Items,
            item => Assert.Equal(progress.Message.Id, Assert.IsType<AssistantMessageItem>(item).MessageId),
            item => Assert.Equal(reasoning.Message.Id, Assert.IsType<ReasoningItem>(item).MessageId));
    }

    [Fact]
    public void Rebuild_ReasoningBeforeLaterTools_DoesNotMoveBackAcrossTheAnswer()
    {
        var turns = CreateBuilder().Rebuild([
            User("Check the result", AskedAt),
            new ChatMessageViewModel(new ChatMessage { Role = "assistant", Content = "The first check passed.", IsProgress = false }),
            Reasoning("Plan another check."),
            Tool("tool-1", "Stopped"),
        ]);

        Assert.Equal("The first check passed.", Assert.IsType<AssistantMessageItem>(turns[^1].Items[0]).Content);
        var laterWork = Assert.IsType<TurnSummaryItem>(turns[^1].Items[1]);
        Assert.IsType<ReasoningItem>(laterWork.InnerItems[0]);
        Assert.IsType<SingleToolItem>(laterWork.InnerItems[1]);
    }

    [Fact]
    public void LateReasoning_PreservesTheAnswersPlanAndModelMarker()
    {
        var (builder, turns) = CreateLiveBuilder();
        builder.ProcessMessageToTranscript(User("Plan the change", AskedAt));
        builder.SetPendingPlanCard("Plan ready", () => { });
        var answer = StreamAssistant(builder, "The plan is ready.", isProgress: false, AskedAt.AddSeconds(10));
        var reasoning = Reasoning("Explain the plan.");
        builder.ProcessMessageToTranscript(reasoning);
        builder.AppendModelLabel("gpt-6.1-sol");

        builder.CollapseCompletedBlocksInCurrentTurn();

        Assert.Collection(turns[^1].Items,
            item => Assert.Equal(reasoning.Message.Id, Assert.IsType<ReasoningItem>(item).MessageId),
            item => Assert.Equal(answer.Message.Id, Assert.IsType<AssistantMessageItem>(item).MessageId),
            item => Assert.IsType<PlanCardItem>(item),
            item => Assert.IsType<TurnModelItem>(item));
    }

    [Fact]
    public void WhileWorking_EveryUpdateStaysInViewUntilTheAnswerFoldsTheWork()
    {
        Lumi.Localization.Loc.Load("en");
        var (builder, turns) = CreateLiveBuilder();
        builder.ProcessMessageToTranscript(User("Fix the build", AskedAt));

        var first = StreamAssistant(builder, "I'll check the build output.", isProgress: true, AskedAt.AddSeconds(2));
        builder.ProcessMessageToTranscript(Tool("tool-1", "Completed"));
        builder.ProcessMessageToTranscript(Reasoning("The using is missing."));
        var second = StreamAssistant(builder, "Now I'll rerun the tests.", isProgress: true, AskedAt.AddSeconds(30));
        var running = Tool("tool-2", "InProgress");
        builder.ProcessMessageToTranscript(running);

        var work = turns[^1];
        Assert.Equal(
            [first.Message.Id, second.Message.Id],
            work.Items.OfType<AssistantMessageItem>().Select(static item => item.MessageId));
        Assert.DoesNotContain(work.Items.OfType<TurnSummaryItem>(), static summary => summary.InnerItems.OfType<AssistantMessageItem>().Any());
        Assert.True(Assert.IsType<ToolGroupItem>(work.Items[^1]).IsActive);

        running.Message.ToolStatus = "Completed";
        running.NotifyToolStatusChanged();
        var answer = StreamAssistant(builder, "Fixed: added the missing using.", isProgress: false, AskedAt.AddSeconds(90));

        var history = Assert.IsType<TurnSummaryItem>(Assert.Single(work.Items, static item => item is TurnSummaryItem));
        Assert.Equal("Worked for 1m 30s", history.Label);
        Assert.Equal(
            [first.Message.Id, second.Message.Id],
            history.InnerItems.OfType<AssistantMessageItem>().Select(static item => item.MessageId));
        Assert.Equal(answer.Message.Id, Assert.IsType<AssistantMessageItem>(work.Items[^1]).MessageId);
    }

    [Fact]
    public void Rebuild_OfAWorkingChat_KeepsItsUpdatesInViewAndTheAnswerFoldsTheWholeTurn()
    {
        Lumi.Localization.Loc.Load("en");
        var builder = CreateBuilder();
        var turns = builder.Rebuild(
            [
                User("Fix the build", AskedAt),
                new ChatMessageViewModel(new ChatMessage { Role = "assistant", Content = "Let me look.", IsProgress = true, Timestamp = AskedAt.AddSeconds(1), Model = "claude-opus-5.5" }),
                Tool("tool-1", "Completed"),
                new ChatMessageViewModel(new ChatMessage { Role = "assistant", Content = "Now the tests.", IsProgress = true, Timestamp = AskedAt.AddSeconds(9), Model = "claude-opus-5.5" }),
                Tool("tool-2", "Completed"),
            ],
            forkOrigin: null,
            isWorking: true);

        var work = turns[^1];
        Assert.Equal(["Let me look.", "Now the tests."], work.Items.OfType<AssistantMessageItem>().Select(static item => item.Content));
        Assert.DoesNotContain(work.Items, static item => item is TurnModelItem or FileChangesSummaryItem);

        // The live work continues the same turn, so the answer folds all of it.
        builder.ProcessMessageToTranscript(Tool("tool-3", "Completed"));
        var answer = StreamAssistant(builder, "All tests pass.", isProgress: false, AskedAt.AddSeconds(40));

        Assert.Same(work, turns[^1]);
        var history = Assert.IsType<TurnSummaryItem>(work.Items[0]);
        Assert.Equal("Worked for 40s", history.Label);
        Assert.Equal(["Let me look.", "Now the tests."], history.InnerItems.OfType<AssistantMessageItem>().Select(static item => item.Content));
        Assert.Equal(answer.Message.Id, Assert.IsType<AssistantMessageItem>(work.Items[^1]).MessageId);
    }

    [Fact]
    public void Rebuild_OfAWorkingChat_WhileAnUpdateStreams_KeepsEveryUpdateInView()
    {
        var builder = CreateBuilder();
        var streaming = new ChatMessageViewModel(new ChatMessage
        {
            Role = "assistant",
            Content = "Now I'll rerun the tests",
            IsStreaming = true,
            Timestamp = AskedAt.AddSeconds(9),
        });
        var turns = builder.Rebuild(
            [
                User("Fix the build", AskedAt),
                new ChatMessageViewModel(new ChatMessage { Role = "assistant", Content = "Let me look.", IsProgress = true, Timestamp = AskedAt.AddSeconds(1) }),
                Tool("tool-1", "Completed"),
                streaming,
            ],
            forkOrigin: null,
            isWorking: true);

        // The message still streaming has no role yet, so it settles nothing before it.
        var work = turns[^1];
        Assert.Equal(["Let me look.", "Now I'll rerun the tests"], work.Items.OfType<AssistantMessageItem>().Select(static item => item.Content));

        streaming.Message.IsProgress = true;
        streaming.Message.IsStreaming = false;
        streaming.NotifyStreamingEnded();

        Assert.Equal(["Let me look.", "Now I'll rerun the tests"], work.Items.OfType<AssistantMessageItem>().Select(static item => item.Content));
    }

    [Fact]
    public void StoppedTurn_KeepsTheLastUpdateVisibleWithoutAWorkedLabel()
    {
        var (builder, turns) = CreateLiveBuilder();
        builder.ProcessMessageToTranscript(User("Fix the build", AskedAt));

        StreamAssistant(builder, "I'll check the build output.", isProgress: true, AskedAt.AddSeconds(2));
        builder.ProcessMessageToTranscript(Tool("tool-1", "Completed"));
        var last = StreamAssistant(builder, "Now I'll rerun the tests.", isProgress: true, AskedAt.AddSeconds(30));
        builder.ProcessMessageToTranscript(Tool("tool-2", "Stopped"));
        builder.CloseCurrentToolGroup();
        builder.CollapseCompletedBlocksInCurrentTurn();

        var work = turns[^1];
        var history = Assert.IsType<TurnSummaryItem>(work.Items[0]);
        Assert.DoesNotContain("Worked for", history.Label);
        Assert.Equal(last.Message.Id, Assert.IsType<AssistantMessageItem>(work.Items[1]).MessageId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public void TurnWithoutAnAnswer_NamesItsModelOnTheTurnLabel(bool? recordedProgress)
    {
        var turns = CreateBuilder().Rebuild([
            User("Fix the build", AskedAt),
            new ChatMessageViewModel(new ChatMessage { Role = "assistant", Content = "Let me look.", IsProgress = recordedProgress, Model = "gpt-5.5" }),
            Tool("tool-1", "Completed"),
            new ChatMessageViewModel(new ChatMessage { Role = "assistant", Content = "Now the tests.", IsProgress = recordedProgress, Model = "gpt-5.5" }),
            Tool("tool-2", "Stopped"),
        ]);

        // Updates show no caption, so the turn's own label names the model.
        Assert.True(Assert.Single(turns[^1].Items.OfType<TurnModelItem>()).IsItemVisible);
    }

    [Fact]
    public void MessageSentWhileWorking_TheRunsEndFoldsTheWorkOnBothSidesOfIt()
    {
        var (builder, turns) = CreateLiveBuilder();
        builder.ProcessMessageToTranscript(User("Fix the build", AskedAt));
        StreamAssistant(builder, "I'll check the build output.", isProgress: true, AskedAt.AddSeconds(2));
        builder.ProcessMessageToTranscript(Tool("tool-1", "Completed"));
        StreamAssistant(builder, "Found it: a missing using.", isProgress: true, AskedAt.AddSeconds(20));
        builder.ProcessMessageToTranscript(Tool("tool-2", "Completed"));
        var beforeMessage = turns[^1];

        builder.ProcessMessageToTranscript(User("Also check the tests", AskedAt.AddSeconds(30)));
        StreamAssistant(builder, "Running the tests too.", isProgress: true, AskedAt.AddSeconds(35));
        builder.ProcessMessageToTranscript(Tool("tool-3", "Completed"));
        var answer = StreamAssistant(builder, "Fixed, and the tests pass.", isProgress: false, AskedAt.AddSeconds(60));
        Assert.Equal(2, beforeMessage.Items.OfType<AssistantMessageItem>().Count());

        builder.CollapseCompletedBlocksInCurrentTurn();

        // The work before the message folds as reopening the chat would fold it, keeping its last update.
        var earlier = Assert.IsType<TurnSummaryItem>(beforeMessage.Items[0]);
        Assert.Equal("I'll check the build output.", Assert.IsType<AssistantMessageItem>(earlier.InnerItems[0]).Content);
        Assert.Equal("Found it: a missing using.", Assert.IsType<AssistantMessageItem>(beforeMessage.Items[1]).Content);
        var afterMessage = turns[^1];
        Assert.IsType<TurnSummaryItem>(afterMessage.Items[0]);
        Assert.Equal(answer.Message.Id, Assert.IsType<AssistantMessageItem>(afterMessage.Items[^1]).MessageId);
    }

    [Fact]
    public void RunThatResumesASettledTurn_FoldsItsNewWorkAtItsOwnEnd()
    {
        var (builder, turns) = CreateLiveBuilder();
        builder.ProcessMessageToTranscript(User("Review the change", AskedAt));
        StreamAssistant(builder, "Starting the reviewers.", isProgress: true, AskedAt.AddSeconds(1));
        builder.ProcessMessageToTranscript(Tool("tool-1", "Completed"));
        StreamAssistant(builder, "I'll report back once they finish.", isProgress: false, AskedAt.AddSeconds(5));
        builder.CollapseCompletedBlocksInCurrentTurn();

        // A background agent wakes Lumi later; its work continues the same turn until Lumi is stopped.
        StreamAssistant(builder, "The reviewers are done; reading their notes.", isProgress: true, AskedAt.AddMinutes(5));
        builder.ProcessMessageToTranscript(Tool("tool-2", "Completed"));
        StreamAssistant(builder, "Now the second file.", isProgress: true, AskedAt.AddMinutes(6));
        builder.ProcessMessageToTranscript(Tool("tool-3", "Stopped"));
        builder.CloseCurrentToolGroup();
        builder.CollapseCompletedBlocksInCurrentTurn();

        Assert.Equal(
            ["I'll report back once they finish.", "Now the second file."],
            turns[^1].Items.OfType<AssistantMessageItem>().Select(static item => item.Content));
    }

    [Fact]
    public void EveryAnswerMessageStaysVisible()
    {
        var (builder, turns) = CreateLiveBuilder();
        builder.ProcessMessageToTranscript(User("Compare the plans", AskedAt));

        StreamAssistant(builder, "I'll pull up both plans.", isProgress: true, AskedAt.AddSeconds(1));
        builder.ProcessMessageToTranscript(Tool("tool-1", "Completed"));
        var first = StreamAssistant(builder, "Plan A costs less.", isProgress: false, AskedAt.AddSeconds(9));
        var second = StreamAssistant(builder, "Plan B covers more.", isProgress: false, AskedAt.AddSeconds(10));
        builder.CollapseCompletedBlocksInCurrentTurn();

        var work = turns[^1];
        Assert.Equal(3, work.Items.Count);
        Assert.Equal("Worked for 9s", Assert.IsType<TurnSummaryItem>(work.Items[0]).Label);
        Assert.Equal(first.Message.Id, Assert.IsType<AssistantMessageItem>(work.Items[1]).MessageId);
        Assert.Equal(second.Message.Id, Assert.IsType<AssistantMessageItem>(work.Items[2]).MessageId);
    }

    [Fact]
    public void Rebuild_FoldsHistoryRecordedBeforeRolesByPosition()
    {
        Lumi.Localization.Loc.Load("en");
        var turns = CreateBuilder().Rebuild([
            User("Fix the build", AskedAt),
            new ChatMessageViewModel(new ChatMessage { Role = "assistant", Content = "Let me look.", Timestamp = AskedAt.AddSeconds(1) }),
            Tool("tool-1", "Completed"),
            new ChatMessageViewModel(new ChatMessage { Role = "assistant", Content = "Fixed it.", Timestamp = AskedAt.AddSeconds(8) }),
        ]);

        var work = turns[^1];
        var history = Assert.IsType<TurnSummaryItem>(work.Items[0]);
        Assert.Equal("Worked for 8s", history.Label);
        Assert.False(history.IsExpanded);
        Assert.Equal("Let me look.", Assert.IsType<AssistantMessageItem>(history.InnerItems[0]).Content);
        Assert.Equal("Fixed it.", Assert.IsType<AssistantMessageItem>(work.Items[1]).Content);
    }

    [Fact]
    public void Rebuild_ReadsAnOlderMessageThatToolsFollowedAsAnUpdate()
    {
        Lumi.Localization.Loc.Load("en");
        var turns = CreateBuilder().Rebuild([
            User("Fix the build", AskedAt),
            new ChatMessageViewModel(new ChatMessage { Role = "assistant", Content = "Let me look.", Timestamp = AskedAt.AddSeconds(1) }),
            Tool("tool-1", "Completed"),
            new ChatMessageViewModel(new ChatMessage { Role = "assistant", Content = "Now the XAML diff:", Timestamp = AskedAt.AddSeconds(8) }),
            Tool("tool-2", "Completed"),
            User("Also check the tests", AskedAt.AddMinutes(1)),
            new ChatMessageViewModel(new ChatMessage { Role = "assistant", Content = "All tests pass.", Timestamp = AskedAt.AddMinutes(1).AddSeconds(5) }),
        ]);

        var cutOff = turns.Single(turn => turn.EnumerateItems().OfType<AssistantMessageItem>().Any(item => item.Content == "Now the XAML diff:"));
        Assert.DoesNotContain(cutOff.Items.OfType<TurnSummaryItem>(), static summary => summary.Label.StartsWith("Worked for", StringComparison.Ordinal));
        var lastUpdate = Assert.Single(cutOff.Items.OfType<AssistantMessageItem>());
        Assert.Equal("Now the XAML diff:", lastUpdate.Content);
        Assert.True(lastUpdate.IsProgress);
        Assert.All(cutOff.EnumerateItems().OfType<AssistantMessageItem>(), static item => Assert.True(item.IsProgress));

        var answer = Assert.IsType<AssistantMessageItem>(turns[^1].Items[^1]);
        Assert.Equal("All tests pass.", answer.Content);
        Assert.False(answer.IsProgress);
    }

    [Fact]
    public void Rebuild_FoldsTheWorkBeforeTheAnswer_PastSkillsItLoadedAndQuestionsItAsked()
    {
        Lumi.Localization.Loc.Load("en");
        var turns = CreateBuilder().Rebuild([
            User("Write the report", AskedAt),
            new ChatMessageViewModel(new ChatMessage { Role = "assistant", Content = "Loading the skill first:", IsProgress = true, Timestamp = AskedAt.AddSeconds(1) }),
            new ChatMessageViewModel(new ChatMessage { Role = "tool", ToolCallId = "tool-1", ToolName = "fetch_skill", ToolStatus = "Completed", Content = "{\"name\":\"Code Helper\"}" }),
            new ChatMessageViewModel(new ChatMessage { Role = "assistant", Content = "One question before I write it:", IsProgress = true, Timestamp = AskedAt.AddSeconds(5) }),
            new ChatMessageViewModel(new ChatMessage { Role = "tool", ToolCallId = "tool-2", ToolName = "ask_question", ToolStatus = "Completed", QuestionText = "Which format?", ToolOutput = "User answered: Word" }),
            new ChatMessageViewModel(new ChatMessage { Role = "assistant", Content = "Writing it now.", IsProgress = true, Timestamp = AskedAt.AddSeconds(30) }),
            Tool("tool-3", "Completed"),
            new ChatMessageViewModel(new ChatMessage { Role = "assistant", Content = "Your report is ready.", IsProgress = false, Timestamp = AskedAt.AddSeconds(60) }),
        ]);

        var work = turns[^1];
        var history = Assert.IsType<TurnSummaryItem>(work.Items[0]);
        Assert.Equal("Worked for 1m", history.Label);
        Assert.Collection(history.InnerItems,
            item => Assert.Equal("Loading the skill first:", Assert.IsType<AssistantMessageItem>(item).Content),
            item => Assert.IsType<SkillLoadedItem>(item),
            item => Assert.Equal("One question before I write it:", Assert.IsType<AssistantMessageItem>(item).Content),
            item => Assert.True(Assert.IsType<QuestionItem>(item).IsAnswered),
            item => Assert.Equal("Writing it now.", Assert.IsType<AssistantMessageItem>(item).Content),
            item => Assert.IsType<SingleToolItem>(item));
        Assert.Equal("Your report is ready.", Assert.IsType<AssistantMessageItem>(Assert.Single(work.Items, static item => item is AssistantMessageItem)).Content);
    }

    [Fact]
    public void TurnEnd_FoldsWorkThatAPlanCardKeepsFromTheAnswer()
    {
        var (builder, turns) = CreateLiveBuilder();
        builder.ProcessMessageToTranscript(User("Plan the migration", AskedAt));
        StreamAssistant(builder, "Drafting the plan first.", isProgress: true, AskedAt.AddSeconds(1));
        builder.ProcessMessageToTranscript(Tool("tool-1", "Completed"));
        builder.SetPendingPlanCard("Plan ready", () => { });
        StreamAssistant(builder, "The plan is ready; starting on it.", isProgress: true, AskedAt.AddSeconds(20));
        builder.ProcessMessageToTranscript(Tool("tool-2", "Completed"));
        var answer = StreamAssistant(builder, "Migrated.", isProgress: false, AskedAt.AddSeconds(60));
        builder.CollapseCompletedBlocksInCurrentTurn();

        // The plan card stays in view to open the plan, and the work on its far side folds anyway.
        var work = turns[^1];
        var earlier = Assert.IsType<TurnSummaryItem>(work.Items[0]);
        Assert.Equal(
            ["Drafting the plan first.", "The plan is ready; starting on it."],
            earlier.InnerItems.OfType<AssistantMessageItem>().Select(static item => item.Content));
        Assert.IsType<PlanCardItem>(work.Items[1]);
        Assert.Equal(answer.Message.Id, Assert.IsType<AssistantMessageItem>(work.Items[^1]).MessageId);
    }

    [Fact]
    public void TurnThatOnlyDeliveredFiles_DoesNotClaimHowLongLumiWorked()
    {
        Lumi.Localization.Loc.Load("en");
        var askedAt = DateTimeOffset.Now.AddMinutes(-5);
        var path = Path.Combine(Path.GetTempPath(), $"lumi-worked-{Guid.NewGuid():N}.docx");
        File.WriteAllText(path, "report");
        try
        {
            var turns = CreateBuilder().Rebuild([
                User("Write the report", askedAt),
                Tool("tool-1", "Completed"),
                Reasoning("Formatting it next."),
                Tool("tool-2", "Completed"),
                new ChatMessageViewModel(new ChatMessage
                {
                    Role = "tool",
                    ToolCallId = "tool-3",
                    ToolName = "announce_file",
                    ToolStatus = "Completed",
                    Content = JsonSerializer.Serialize(new { filePath = path }),
                }),
            ]);

            // The bubble that delivers the file has no text, and is dated when the chat was reopened.
            var work = turns[^1];
            Assert.True(Assert.IsType<AssistantMessageItem>(work.Items[^1]).HasFileAttachments);
            Assert.DoesNotContain("Worked for", Assert.IsType<TurnSummaryItem>(work.Items[0]).Label);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Rebuild_FoldsAnUpdateRightBeforeTheAnswer()
    {
        var turns = CreateBuilder().Rebuild([
            User("Hi", AskedAt),
            new ChatMessageViewModel(new ChatMessage { Role = "assistant", Content = "One moment.", IsProgress = true, Timestamp = AskedAt.AddSeconds(1) }),
            new ChatMessageViewModel(new ChatMessage { Role = "assistant", Content = "Hello!", IsProgress = false, Timestamp = AskedAt.AddSeconds(3) }),
        ]);

        var work = turns[^1];
        var history = Assert.IsType<TurnSummaryItem>(work.Items[0]);
        Assert.Equal("One moment.", Assert.IsType<AssistantMessageItem>(Assert.Single(history.InnerItems)).Content);
        Assert.Equal("Hello!", Assert.IsType<AssistantMessageItem>(work.Items[1]).Content);
    }

    [Fact]
    public void FoldingDisabled_KeepsEveryMessageAsABoundary()
    {
        var turns = CreateBuilder(foldProgressMessages: false).Rebuild([
            User("Fix the build", AskedAt),
            new ChatMessageViewModel(new ChatMessage { Role = "assistant", Content = "Let me look.", IsProgress = true }),
            Tool("tool-1", "Completed"),
            new ChatMessageViewModel(new ChatMessage { Role = "assistant", Content = "Fixed it.", IsProgress = false }),
        ]);

        var work = turns[^1];
        Assert.Collection(work.Items,
            item => Assert.Equal("Let me look.", Assert.IsType<AssistantMessageItem>(item).Content),
            item => Assert.IsType<SingleToolItem>(item),
            item => Assert.Equal("Fixed it.", Assert.IsType<AssistantMessageItem>(item).Content));
    }

    [Fact]
    public void ProgressUpdates_LeaveCreatedFilesForTheAnswer()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lumi-worked-{Guid.NewGuid():N}.docx");
        File.WriteAllText(path, "report");
        try
        {
            var (builder, turns) = CreateLiveBuilder();
            builder.ProcessMessageToTranscript(User("Write the report", AskedAt));
            builder.AddDetectedFileChip(path);
            var update = new ChatMessageViewModel(new ChatMessage { Role = "assistant", Content = "Formatting it now.", IsProgress = true });
            builder.ProcessMessageToTranscript(update);
            var answer = new ChatMessageViewModel(new ChatMessage { Role = "assistant", Content = "Your report is ready.", IsProgress = false });
            builder.ProcessMessageToTranscript(answer);

            var items = turns[^1].EnumerateItems().OfType<AssistantMessageItem>().ToList();
            Assert.False(items.Single(item => item.MessageId == update.Message.Id).HasFileAttachments);
            var shown = Assert.IsType<AssistantMessageItem>(turns[^1].Items[^1]);
            Assert.Equal(answer.Message.Id, shown.MessageId);
            Assert.True(shown.HasFileAttachments);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DemotedPiece_IsRestyledAndFoldsWithTheWork()
    {
        var (builder, turns) = CreateLiveBuilder();
        builder.ProcessMessageToTranscript(User("Check it", AskedAt));
        var piece = StreamAssistant(builder, "Looking now.", isProgress: false, AskedAt.AddSeconds(1));
        var item = Assert.IsType<AssistantMessageItem>(turns[^1].Items[^1]);
        Assert.False(item.IsProgress);

        piece.Message.IsProgress = true;
        builder.RefreshAssistantProgress([piece.Message]);
        builder.ProcessMessageToTranscript(Tool("tool-1", "Completed"));
        StreamAssistant(builder, "It works.", isProgress: false, AskedAt.AddSeconds(5));

        Assert.True(item.IsProgress);
        Assert.Contains(item, Assert.IsType<TurnSummaryItem>(turns[^1].Items[0]).InnerItems);
    }

    [Fact]
    public void TurnSummary_ReadersToggleOverridesLumisAutoExpansion()
    {
        var summary = new TurnSummaryItem("Plan");

        summary.SetAutoExpanded(true);
        Assert.True(summary.IsExpanded);
        Assert.True(summary.IsAutoExpanded);

        summary.IsExpanded = false;
        Assert.False(summary.IsAutoExpanded);
        summary.IsExpanded = true;
        Assert.False(summary.IsAutoExpanded);
    }

    [Fact]
    public void TranscriptTurn_FindsAndRevealsFoldedItems()
    {
        var turn = new TranscriptTurn("turn:test");
        var folded = new TurnModelItem("folded");
        var summary = new TurnSummaryItem("Worked for 3s");
        summary.InnerItems.Add(folded);
        var shown = new TurnModelItem("shown");
        turn.Items.Add(summary);
        turn.Items.Add(shown);

        Assert.Equal(new TranscriptItem[] { summary, folded, shown }, turn.EnumerateItems().ToList());
        Assert.False(turn.RevealFoldedItem(shown));
        Assert.False(summary.IsExpanded);
        Assert.True(turn.RevealFoldedItem(folded));
        Assert.True(summary.IsExpanded);
    }

    [Theory]
    [InlineData(0.4, "1s")]
    [InlineData(8, "8s")]
    [InlineData(102, "1m 42s")]
    [InlineData(120, "2m")]
    [InlineData(3600, "1h")]
    [InlineData(3900, "1h 5m")]
    public void FormatWorkDuration_IsCompact(double seconds, string expected)
        => Assert.Equal(expected, TranscriptBuilder.FormatWorkDuration(TimeSpan.FromSeconds(seconds)));

    private static ChatMessage Assistant(string content, string? apiCallId = null)
        => new() { Role = "assistant", Content = content, ApiCallId = apiCallId };

    private static ChatMessageViewModel User(string content, DateTimeOffset at)
        => new(new ChatMessage { Role = "user", Content = content, Timestamp = at });

    private static ChatMessageViewModel Tool(string toolCallId, string status)
        => new(new ChatMessage
        {
            Role = "tool",
            ToolCallId = toolCallId,
            ToolName = "view",
            ToolStatus = status,
            Content = "{\"path\":\"E:\\\\repo\\\\README.md\"}",
        });

    private static ChatMessageViewModel Reasoning(string content, bool isStreaming = false)
        => new(new ChatMessage { Role = "reasoning", Author = "Thinking", Content = content, IsStreaming = isStreaming });

    /// <summary>Streams an assistant message and finishes it the way the session does: role first, then the end.</summary>
    private static ChatMessageViewModel StreamAssistant(TranscriptBuilder builder, string content, bool isProgress, DateTimeOffset at)
    {
        var message = new ChatMessageViewModel(new ChatMessage
        {
            Role = "assistant",
            Author = "Lumi",
            Content = content,
            IsStreaming = true,
            Timestamp = at,
        });
        builder.ProcessMessageToTranscript(message);
        message.Message.IsProgress = isProgress;
        message.Message.IsStreaming = false;
        message.NotifyStreamingEnded();
        return message;
    }

    private static (TranscriptBuilder Builder, ObservableCollection<TranscriptTurn> Turns) CreateLiveBuilder()
    {
        var builder = CreateBuilder();
        var turns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(turns);
        return (builder, turns);
    }

    private static TranscriptBuilder CreateBuilder(bool foldProgressMessages = true)
        => new(CreateDataStore(), _ => { }, (_, _) => { }, _ => { }, (_, _) => Task.CompletedTask, () => null,
            foldProgressMessages: foldProgressMessages);

    private static DataStore CreateDataStore()
    {
#pragma warning disable SYSLIB0050
        var store = (DataStore)FormatterServices.GetUninitializedObject(typeof(DataStore));
#pragma warning restore SYSLIB0050
        var data = new AppData();
        data.Skills.Add(new Skill { Name = "Code Helper" });
        typeof(DataStore)
            .GetField("_data", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(store, data);
        return store;
    }
}
