using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Threading.Tasks;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class TranscriptBuilderToolGroupTests
{
    [Theory]
    [InlineData("Failed", "External tool request received no response within 1800 seconds.", false)]
    [InlineData("Stopped", null, false)]
    [InlineData("Completed", "User answered: Yes", true)]
    [InlineData("Completed", "Yes", true)]
    public void Rebuild_QuestionFailureIsExpired_NotAnAnswer(string status, string? output, bool answered)
    {
        var message = CreateToolVm("question-1", "ask_question", status, "{}");
        message.Message.QuestionId = "question-1";
        message.Message.QuestionText = "Approve the rebase?";
        message.Message.QuestionOptions = "[\"Yes\",\"No\"]";
        message.Message.ToolOutput = output;

        var turns = CreateBuilder().Rebuild([message]);

        var question = Assert.IsType<QuestionItem>(Assert.Single(Assert.Single(turns).Items));
        Assert.Equal(answered, question.IsAnswered);
        Assert.Equal(!answered, question.IsExpired);
        Assert.Equal(answered ? "Yes" : null, question.SelectedAnswer);
    }

    [Fact]
    public void NativeSkill_UsesPersistedDisplayNameInTitleAndDetails()
    {
        var builder = CreateBuilder();
        var turns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(turns);
        var message = CreateToolVm("skill-1", "skill", "InProgress", "{\"skill\":\"native-skill-trial\"}");
        message.Message.ToolSkillName = "Native Skill Trial";
        builder.ProcessMessageToTranscript(message);

        var group = Assert.IsType<ToolGroupItem>(Assert.Single(turns[0].Items));
        var call = Assert.IsType<ToolCallItem>(Assert.Single(group.ToolCalls));
        Assert.Contains("Using Native Skill Trial", call.ToolName);
        Assert.Equal("**Skill:** Native Skill Trial", call.InputParameters);
        Assert.Equal("{\"skill\":\"native-skill-trial\"}", message.Content);
    }

    [Fact]
    public void UnopenedStandaloneCommands_StillFoldIntoTheActivityTrail()
    {
        var builder = CreateBuilder();
        var turns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(turns);
        for (var i = 0; i < 3; i++)
        {
            builder.ProcessMessageToTranscript(CreateToolVm($"shell-{i}", "powershell", "Completed", "{}"));
            builder.ProcessMessageToTranscript(CreateReasoningVm($"Check {i}"));
        }
        builder.ProcessMessageToTranscript(CreateToolVm("current", "view", "InProgress", "{}"));

        Assert.Equal(2, turns[0].Items.Count);
        Assert.Equal(6, Assert.IsType<TurnSummaryItem>(turns[0].Items[0]).InnerItems.Count);
    }

    [Theory]
    [InlineData("view", false)]
    [InlineData("view", true)]
    [InlineData("powershell", false)]
    [InlineData("powershell", true)]
    public void ExpandedStandaloneTool_StaysVisibleAfterReasoningAndCompletion(string toolName, bool finishAfterReasoning)
    {
        var builder = CreateBuilder();
        var turns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(turns);
        var message = CreateToolVm("first", toolName, "InProgress", "{}");
        builder.ProcessMessageToTranscript(message);
        var group = Assert.IsType<ToolGroupItem>(Assert.Single(turns[0].Items));
        SetExpanded(group.SingleTool!);

        if (!finishAfterReasoning)
        {
            message.Message.ToolStatus = "Completed";
            message.NotifyToolStatusChanged();
        }
        builder.ProcessMessageToTranscript(CreateReasoningVm("Continue checking"));
        if (finishAfterReasoning)
        {
            message.Message.ToolStatus = "Completed";
            message.NotifyToolStatusChanged();
        }
        builder.ProcessMessageToTranscript(CreateToolVm("next", "view", "InProgress", "{}"));

        Assert.True(Assert.IsType<SingleToolItem>(turns[0].Items[0]).IsExpanded);
        Assert.IsType<ReasoningItem>(turns[0].Items[1]);
        Assert.True(Assert.IsType<ToolGroupItem>(turns[0].Items[2]).IsActive);

        static void SetExpanded(ToolCallItemBase tool)
        {
            if (tool is ToolCallItem call)
                call.IsExpanded = true;
            else
                Assert.IsType<TerminalPreviewItem>(tool).IsExpanded = true;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExpandedStandaloneTool_PromotionPreservesTheInspectedChild(bool terminal)
    {
        var group = new ToolGroupItem("Working");
        ToolCallItemBase first = terminal
            ? new TerminalPreviewItem("Run command", "echo test", StrataTheme.Controls.StrataAiToolCallStatus.InProgress) { IsExpanded = true }
            : new ToolCallItem("Read file", StrataTheme.Controls.StrataAiToolCallStatus.InProgress) { IsExpanded = true };
        group.ToolCalls.Add(first);
        group.ToolCalls.Add(new ToolCallItem("Next action", StrataTheme.Controls.StrataAiToolCallStatus.InProgress));

        Assert.True(group.IsExpanded);
        Assert.True(first is ToolCallItem call ? call.IsExpanded : ((TerminalPreviewItem)first).IsExpanded);
        Assert.False(group.IsSingleTool);
    }

    [Fact]
    public void Rebuild_ConsecutiveReasoning_IsOneItemWithoutASummaryWrapper()
    {
        var first = CreateReasoningVm("Inspect the implementation.");
        var turns = CreateBuilder().Rebuild(
        [
            first,
            CreateReasoningVm(""),
            CreateReasoningVm("Then check the tests."),
        ]);

        var reasoning = Assert.IsType<ReasoningItem>(Assert.Single(Assert.Single(turns).Items));
        Assert.Equal($"message:reasoning:{first.Message.Id}", reasoning.StableId);
        Assert.Equal("Inspect the implementation.\n\nThen check the tests.", reasoning.Content);
    }

    [Fact]
    public void ConsecutiveReasoning_StreamsAllPartsWithoutLosingEarlierText()
    {
        var builder = CreateBuilder();
        var turns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(turns);
        var first = CreateReasoningVm("First", isStreaming: true);
        var second = CreateReasoningVm("", isStreaming: true);

        builder.ProcessMessageToTranscript(first);
        var reasoning = Assert.IsType<ReasoningItem>(Assert.Single(Assert.Single(turns).Items));
        builder.ProcessMessageToTranscript(second);
        builder.ProcessMessageToTranscript(second);
        second.Message.Content = "Second";
        second.NotifyContentChanged();
        first.Message.Content = "First, finalized";
        first.Message.IsStreaming = false;
        first.NotifyStreamingEnded();

        Assert.Same(reasoning, Assert.Single(turns[0].Items));
        Assert.Equal("First, finalized\n\nSecond", reasoning.Content);
        Assert.True(reasoning.IsActive);

        second.Message.IsStreaming = false;
        second.NotifyStreamingEnded();
        Assert.False(reasoning.IsActive);
    }

    [Fact]
    public void ResetState_DetachesMergedReasoningStreams()
    {
        var builder = CreateBuilder();
        var turns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(turns);
        var first = CreateReasoningVm("First", isStreaming: true);
        var second = CreateReasoningVm("Second", isStreaming: true);
        builder.ProcessMessageToTranscript(first);
        builder.ProcessMessageToTranscript(second);
        var reasoning = Assert.IsType<ReasoningItem>(Assert.Single(Assert.Single(turns).Items));

        builder.ResetState();
        first.Message.Content = "Detached first";
        first.NotifyContentChanged();
        second.Message.Content = "Detached second";
        second.NotifyContentChanged();

        Assert.Equal("First\n\nSecond", reasoning.Content);
    }

    [Fact]
    public void ConsecutiveReasoning_AfterExplicitCollapse_RevealsTheMergedLiveItem()
    {
        var builder = CreateBuilder();
        var turns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(turns);
        builder.ProcessMessageToTranscript(CreateToolVm("read", "view", "Completed", "{}"));
        builder.ProcessMessageToTranscript(CreateReasoningVm("First thought"));
        builder.CollapseCompletedBlocksInCurrentTurn();
        Assert.IsType<TurnSummaryItem>(Assert.Single(turns[0].Items));

        builder.ProcessMessageToTranscript(CreateReasoningVm("Continued thought", isStreaming: true));

        var reasoning = Assert.IsType<ReasoningItem>(turns[0].Items[^1]);
        Assert.True(reasoning.IsActive);
        Assert.Equal("First thought\n\nContinued thought", reasoning.Content);
    }

    [Fact]
    public void ConsecutiveReasoning_AfterRebuild_RevealsTheMergedLiveItem()
    {
        var builder = CreateBuilder();
        var turns = builder.Rebuild(
        [
            CreateToolVm("read", "view", "Completed", "{}"),
            CreateReasoningVm("Earlier thought"),
        ]);
        Assert.IsType<TurnSummaryItem>(Assert.Single(turns[0].Items));

        builder.ProcessMessageToTranscript(CreateReasoningVm("Continued thought", isStreaming: true));
        var reasoning = Assert.IsType<ReasoningItem>(turns[0].Items[^1]);
        Assert.True(reasoning.IsActive);
        Assert.Equal("Earlier thought\n\nContinued thought", reasoning.Content);

        builder.ProcessMessageToTranscript(CreateToolVm("next", "view", "InProgress", "{}"));
        Assert.Single(turns);
        Assert.True(Assert.IsType<ToolGroupItem>(turns[0].Items[^1]).IsActive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConsecutiveReasoning_RespectsStreamingExpansionPreference(bool expand)
    {
        var first = CreateReasoningVm("First", isStreaming: true);
        var reasoning = new ReasoningItem(first, expand);
        Assert.Equal(expand, reasoning.IsExpanded);
        first.Message.IsStreaming = false;
        first.NotifyStreamingEnded();
        Assert.False(reasoning.IsExpanded);

        var second = CreateReasoningVm("Second", isStreaming: true);
        reasoning.AppendSource(second);
        Assert.Equal(expand, reasoning.IsExpanded);
        second.Message.IsStreaming = false;
        second.NotifyStreamingEnded();
        Assert.False(reasoning.IsExpanded);
    }

    [Fact]
    public void Rebuild_HiddenReasoningDoesNotProduceAnEmptyHistoryRow()
    {
        var store = CreateDataStore();
        store.Data.Settings.ShowReasoning = false;
        var builder = new TranscriptBuilder(store, _ => { }, (_, _) => { }, _ => { },
            (_, _) => Task.CompletedTask, () => null);

        Assert.Empty(builder.Rebuild([CreateReasoningVm("First"), CreateReasoningVm("Second")]));
    }

    [Fact]
    public void SingleTool_RemainsStandaloneUntilASecondToolActuallyArrives()
    {
        var builder = CreateBuilder();
        var turns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(turns);
        var first = CreateToolVm("first", "view", "InProgress", "{}");
        builder.ProcessMessageToTranscript(first);
        var group = Assert.IsType<ToolGroupItem>(Assert.Single(turns[0].Items));
        var single = Assert.IsType<ToolCallItem>(group.SingleTool);
        Assert.True(group.IsSingleTool);

        first.Message.ToolStatus = "Completed";
        first.NotifyToolStatusChanged();
        Assert.True(group.IsSingleTool);
        Assert.Same(single, group.SingleTool);
        Assert.Equal(StrataTheme.Controls.StrataAiToolCallStatus.Completed, single.Status);

        builder.ProcessMessageToTranscript(CreateToolVm("second", "view", "InProgress", "{}"));
        Assert.Same(group, Assert.Single(turns[0].Items));
        Assert.False(group.IsSingleTool);
        Assert.Null(group.SingleTool);
        Assert.Equal(2, group.ToolCalls.Count);
    }

    [Fact]
    public void SinglePlan_PreservesItsStepProgressHeader()
    {
        var group = new ToolGroupItem("Plan") { Meta = "1/3 completed" };
        group.ToolCalls.Add(new TodoProgressItem("Plan", StrataTheme.Controls.StrataAiToolCallStatus.InProgress));
        Assert.False(group.IsSingleTool);
        Assert.Null(group.SingleTool);
        Assert.Equal("1/3 completed", group.Meta);
    }

    [Fact]
    public void ExplicitCollapse_DoesNotHideAnOpenGroupThatCanReceiveMoreTools()
    {
        var builder = CreateBuilder();
        var turns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(turns);
        builder.ProcessMessageToTranscript(CreateReasoningVm("Planning"));
        builder.ProcessMessageToTranscript(CreateToolVm("first", "view", "Completed", "{}"));
        var group = Assert.IsType<ToolGroupItem>(turns[0].Items[^1]);

        builder.CollapseCompletedBlocksInCurrentTurn();
        builder.ProcessMessageToTranscript(CreateToolVm("second", "view", "InProgress", "{}"));

        Assert.Contains(group, turns[0].Items);
        Assert.True(group.IsActive);
        Assert.Equal(2, group.ToolCalls.Count);
        Assert.DoesNotContain(turns[0].Items, item => item is TurnSummaryItem);
    }

    [Fact]
    public void AlternatingActivity_KeepsOneHistoryRowAndTheCurrentStep()
    {
        var builder = CreateBuilder();
        var turns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(turns);
        for (var i = 0; i < 24; i++)
        {
            builder.ProcessMessageToTranscript(CreateToolVm($"tool-{i}", "view", "Completed", "{}"));
            builder.ProcessMessageToTranscript(CreateReasoningVm($"Reasoning {i}"));
            builder.ProcessMessageToTranscript(CreateReasoningVm($"More reasoning {i}"));
        }
        builder.ProcessMessageToTranscript(CreateToolVm("current", "view", "InProgress", "{}"));

        var turn = Assert.Single(turns);
        Assert.Equal(2, turn.Items.Count);
        var history = Assert.IsType<TurnSummaryItem>(turn.Items[0]);
        Assert.False(history.IsExpanded);
        Assert.Equal(48, history.InnerItems.Count);
        Assert.Equal(24, history.InnerItems.OfType<ReasoningItem>().Count());
        Assert.Equal(24, history.InnerItems.OfType<SingleToolItem>().Count());
        Assert.Contains("24 actions", history.Label);
        Assert.StartsWith("Earlier work", history.Label);
        var current = Assert.IsType<ToolGroupItem>(turn.Items[1]);
        Assert.True(current.IsActive);
        Assert.True(current.IsSingleTool);
    }

    [Fact]
    public void ActivityHistory_PreservesExpandedRowAndChildrenAsWorkArrivesAndFinishes()
    {
        var builder = CreateBuilder();
        var turns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(turns);
        builder.ProcessMessageToTranscript(CreateToolVm("first", "view", "Completed", "{}"));
        builder.ProcessMessageToTranscript(CreateReasoningVm("Reasoning"));
        builder.ProcessMessageToTranscript(CreateToolVm("second", "view", "Completed", "{}"));
        var history = Assert.IsType<TurnSummaryItem>(turns[0].Items[0]);
        var reasoning = Assert.IsType<ReasoningItem>(history.InnerItems[1]);
        history.IsExpanded = true;
        reasoning.IsExpanded = true;

        builder.ProcessMessageToTranscript(CreateReasoningVm("Later reasoning"));
        builder.ProcessMessageToTranscript(CreateAssistantVm("Done."));

        Assert.Same(history, turns[0].Items[0]);
        Assert.True(history.IsExpanded);
        Assert.Same(reasoning, history.InnerItems[1]);
        Assert.True(reasoning.IsExpanded);
        Assert.Equal(4, history.InnerItems.Count);
        Assert.IsType<AssistantMessageItem>(turns[0].Items[^1]);
    }

    [Fact]
    public void ActivityHistory_CompletionKeepsAnExpandedStepVisibleWhenJoiningCollapsedHistory()
    {
        var builder = CreateBuilder();
        var turns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(turns);
        builder.ProcessMessageToTranscript(CreateToolVm("first", "view", "Completed", "{}"));
        builder.ProcessMessageToTranscript(CreateReasoningVm("Reasoning"));
        builder.ProcessMessageToTranscript(CreateToolVm("second", "view", "Completed", "{}"));
        builder.ProcessMessageToTranscript(CreateToolVm("third", "view", "Completed", "{}"));
        var history = Assert.IsType<TurnSummaryItem>(turns[0].Items[0]);
        Assert.False(history.IsExpanded);
        Assert.IsType<ToolGroupItem>(turns[0].Items[1]).IsExpanded = true;

        builder.ProcessMessageToTranscript(CreateAssistantVm("Done."));

        Assert.Same(history, turns[0].Items[0]);
        Assert.True(history.IsExpanded);
        Assert.True(Assert.IsType<ToolGroupItem>(history.InnerItems[^1]).IsExpanded);
    }

    [Fact]
    public void ActivityHistory_DoesNotHideRunningToolsOrStreamingReasoning()
    {
        var builder = CreateBuilder();
        var turns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(turns);
        builder.ProcessMessageToTranscript(CreateToolVm("running", "view", "InProgress", "{}"));
        builder.ProcessMessageToTranscript(CreateReasoningVm("Still thinking", isStreaming: true));
        builder.ProcessMessageToTranscript(CreateToolVm("completed", "view", "Completed", "{}"));
        builder.CollapseCompletedBlocksInCurrentTurn();

        Assert.Equal(3, turns[0].Items.Count);
        Assert.True(Assert.IsType<ToolGroupItem>(turns[0].Items[0]).IsActive);
        Assert.True(Assert.IsType<ReasoningItem>(turns[0].Items[1]).IsActive);
        Assert.DoesNotContain(turns[0].Items, item => item is TurnSummaryItem);
    }

    [Fact]
    public void ActivityHistory_LateToolCompletionFoldsOnlyTheFinishedPrefix()
    {
        var builder = CreateBuilder();
        var turns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(turns);
        var pending = CreateToolVm("pending", "view", "InProgress", "{}");
        builder.ProcessMessageToTranscript(pending);
        builder.ProcessMessageToTranscript(CreateReasoningVm("Thinking between actions"));
        builder.ProcessMessageToTranscript(CreateToolVm("current", "view", "InProgress", "{}"));
        Assert.Equal(3, turns[0].Items.Count);

        pending.Message.ToolStatus = "Completed";
        pending.NotifyToolStatusChanged();

        Assert.Equal(2, turns[0].Items.Count);
        Assert.IsType<TurnSummaryItem>(turns[0].Items[0]);
        Assert.True(Assert.IsType<ToolGroupItem>(turns[0].Items[1]).IsActive);
    }

    [Fact]
    public void ActivityHistory_DoesNotFoldAnExpandedReasoningItemWhileWorking()
    {
        var builder = CreateBuilder();
        var turns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(turns);
        builder.ProcessMessageToTranscript(CreateToolVm("first", "view", "Completed", "{}"));
        builder.ProcessMessageToTranscript(CreateReasoningVm("Inspecting this thought"));
        var reasoning = Assert.IsType<ReasoningItem>(turns[0].Items[^1]);
        reasoning.IsExpanded = true;

        builder.ProcessMessageToTranscript(CreateToolVm("second", "view", "InProgress", "{}"));

        Assert.Contains(reasoning, turns[0].Items);
        Assert.True(reasoning.IsExpanded);
        Assert.DoesNotContain(turns[0].Items, item => item is TurnSummaryItem);
    }

    [Fact]
    public void ActivityHistory_KeepsQuestionsOutsideTheFoldAndShowsFailures()
    {
        var builder = CreateBuilder();
        var turns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(turns);
        builder.ProcessMessageToTranscript(CreateToolVm("failed", "view", "Failed", "{}"));
        builder.ProcessMessageToTranscript(CreateReasoningVm("Need more information"));
        builder.AddQuestionToTranscript("question", "Which file?", ["A", "B"], false);

        var history = Assert.IsType<TurnSummaryItem>(turns[0].Items[0]);
        Assert.True(history.HasFailures);
        Assert.Contains("1 failed", history.Label);
        Assert.IsType<QuestionItem>(turns[0].Items[1]);
        builder.ProcessMessageToTranscript(CreateReasoningVm("After question"));
        Assert.IsType<QuestionItem>(turns[0].Items[1]);
        Assert.IsType<ReasoningItem>(turns[0].Items[2]);
    }

    [Fact]
    public void ActivityHistory_LateBackgroundShellIsRevealedInChronologicalOrder()
    {
        var builder = CreateBuilder();
        var turns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(turns);
        builder.ProcessMessageToTranscript(CreateToolVm("shell", "powershell", "Completed", "{\"command\":\"long job\"}"));
        builder.ProcessMessageToTranscript(CreateReasoningVm("The shell is running independently"));
        builder.ProcessMessageToTranscript(CreateToolVm("current", "view", "InProgress", "{}"));
        Assert.IsType<TurnSummaryItem>(turns[0].Items[0]);

        builder.SetTerminalRunningInBackground("shell", true);

        var group = Assert.IsType<ToolGroupItem>(turns[0].Items[0]);
        var terminal = Assert.IsType<TerminalPreviewItem>(group.SingleTool);
        Assert.True(terminal.IsRunningInBackground);
        Assert.True(terminal.IsExpanded);
        Assert.Equal("long job", Assert.Single(group.ActivityPreview).Detail);
        Assert.IsType<ReasoningItem>(turns[0].Items[1]);
        Assert.True(Assert.IsType<ToolGroupItem>(turns[0].Items[2]).IsActive);
    }

    [Fact]
    public void ActivityHistory_RespectsSubagentShowAllWorkMode()
    {
        var builder = new TranscriptBuilder(CreateDataStore(), _ => { }, (_, _) => { }, _ => { },
            (_, _) => Task.CompletedTask, () => null) { CollapseCompletedTurns = false };
        var turns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(turns);
        builder.ProcessMessageToTranscript(CreateToolVm("first", "view", "Completed", "{}"));
        builder.ProcessMessageToTranscript(CreateReasoningVm("Thought one"));
        builder.ProcessMessageToTranscript(CreateReasoningVm("Thought two"));
        builder.ProcessMessageToTranscript(CreateToolVm("second", "view", "Completed", "{}"));

        Assert.Equal(3, turns[0].Items.Count);
        Assert.DoesNotContain(turns[0].Items, item => item is TurnSummaryItem);
        Assert.Equal("Thought one\n\nThought two", Assert.IsType<ReasoningItem>(turns[0].Items[1]).Content);
    }

    [Fact]
    public void ProcessMessageToTranscript_SameMessageTwice_RendersOnce()
    {
        var builder = CreateBuilder();
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);
        var message = new ChatMessageViewModel(new ChatMessage
        {
            Role = "user",
            Author = "You",
            Content = "Do not render me twice.",
            Timestamp = DateTimeOffset.Now,
        });

        builder.ProcessMessageToTranscript(message);
        builder.ProcessMessageToTranscript(message);

        var turn = Assert.Single(liveTurns);
        Assert.IsType<UserMessageItem>(Assert.Single(turn.Items));
    }

    [Fact]
    public void ProcessMessageToTranscript_StreamingToolGroup_ShowsOnlyRunningActivityRows()
    {
        var builder = CreateBuilder();
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        var firstTool = CreateToolVm("tool-1", "view", "InProgress", "{\"path\":\"E:\\\\repo\\\\notes.txt\"}");
        var secondTool = CreateToolVm("tool-2", "powershell", "InProgress", "{\"command\":\"dotnet test\"}");

        builder.ProcessMessageToTranscript(firstTool);
        builder.ProcessMessageToTranscript(secondTool);

        var turn = Assert.Single(liveTurns);
        var group = Assert.IsType<ToolGroupItem>(Assert.Single(turn.Items));

        Assert.True(group.IsActive);
        Assert.False(group.IsExpanded);
        Assert.Equal(2, group.ToolCalls.Count);
        Assert.Equal(2, group.ActivityPreview.Count);
        Assert.Contains("notes.txt", group.ActivityPreview[0].Label + group.ActivityPreview[0].Detail, StringComparison.Ordinal);
        Assert.Contains("Running command", group.ActivityPreview[1].Label, StringComparison.Ordinal);
        Assert.Equal("dotnet test", group.ActivityPreview[1].Detail);

        firstTool.Message.ToolStatus = "Completed";
        firstTool.NotifyToolStatusChanged();
        Assert.True(group.IsActive);
        Assert.Equal("dotnet test", Assert.Single(group.ActivityPreview).Detail);
        Assert.Equal(50, group.ProgressValue);

        secondTool.Message.ToolStatus = "Completed";
        secondTool.NotifyToolStatusChanged();

        Assert.False(group.IsActive);
        Assert.Empty(group.ActivityPreview);
        Assert.Null(group.AdditionalActivityLabel);
    }

    [Fact]
    public void StreamingActivityPreview_IsBoundedAndKeepsEarlierRunningOperationsVisible()
    {
        var builder = CreateBuilder();
        var turns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(turns);
        var messages = Enumerable.Range(1, 5)
            .Select(index => CreateToolVm($"tool-{index}", "view", "InProgress",
                $"{{\"path\":\"file-{index}.txt\"}}"))
            .ToArray();
        foreach (var message in messages)
            builder.ProcessMessageToTranscript(message);

        var group = Assert.IsType<ToolGroupItem>(Assert.Single(Assert.Single(turns).Items));
        Assert.Equal(3, group.ActivityPreview.Count);
        Assert.Equal(2, group.AdditionalActivityCount);
        Assert.Equal("+2 more running", group.AdditionalActivityLabel);
        Assert.Contains("file-1.txt", group.ActivityPreview[0].Label + group.ActivityPreview[0].Detail);
        var unchangedPreview = group.ActivityPreview;

        foreach (var status in new[] { "Completed", "Failed", "Stopped" })
            builder.ProcessMessageToTranscript(CreateToolVm(status, "view", status, "{\"path\":\"finished.txt\"}"));
        Assert.Same(unchangedPreview, group.ActivityPreview);
        Assert.Equal(2, group.AdditionalActivityCount);

        messages[0].Message.ToolStatus = "Completed";
        messages[0].NotifyToolStatusChanged();
        Assert.Equal(1, group.AdditionalActivityCount);
        Assert.Contains("file-2.txt", group.ActivityPreview[0].Label + group.ActivityPreview[0].Detail);
        Assert.Contains("file-4.txt", group.ActivityPreview[2].Label + group.ActivityPreview[2].Detail);

        foreach (var message in messages.Skip(1))
        {
            message.Message.ToolStatus = "Stopped";
            message.NotifyToolStatusChanged();
        }
        Assert.False(group.IsActive);
        Assert.Empty(group.ActivityPreview);
        Assert.Equal(0, group.AdditionalActivityCount);
        Assert.Null(group.AdditionalActivityLabel);
    }

    [Fact]
    public void ToolGroup_Expanding_CollapsesNestedTools()
    {
        var group = new ToolGroupItem("Finished");
        var tool = new ToolCallItem("Read file", StrataTheme.Controls.StrataAiToolCallStatus.Completed)
        {
            IsExpanded = true,
        };
        var terminal = new TerminalPreviewItem(
            "Run command",
            "dotnet test",
            StrataTheme.Controls.StrataAiToolCallStatus.Completed)
        {
            IsExpanded = true,
        };
        var todo = new TodoProgressItem("Update plan", StrataTheme.Controls.StrataAiToolCallStatus.Completed)
        {
            IsExpanded = true,
        };

        group.ToolCalls.Add(tool);
        group.ToolCalls.Add(terminal);
        group.ToolCalls.Add(todo);

        group.IsExpanded = false;
        group.IsExpanded = true;

        Assert.False(tool.IsExpanded);
        Assert.False(terminal.IsExpanded);
        Assert.False(todo.IsExpanded);

        tool.IsExpanded = true;
        group.IsExpanded = false;
        group.IsExpanded = true;

        Assert.False(tool.IsExpanded);
    }

    [Fact]
    public void ProcessMessageToTranscript_FailedTerminalStatusUpdateShowsCapturedOutput()
    {
        var builder = CreateBuilder();
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        var toolVm = CreateToolVm("tool-1", "powershell", "InProgress", "{\"command\":\"dotnet test\"}");
        builder.ProcessMessageToTranscript(toolVm);

        var group = Assert.IsType<ToolGroupItem>(Assert.Single(Assert.Single(liveTurns).Items));
        var terminal = Assert.IsType<TerminalPreviewItem>(Assert.Single(group.ToolCalls));
        Assert.Empty(terminal.Output);

        toolVm.Message.ToolOutput = "Command failed with exit code 1.";
        toolVm.Message.ToolStatus = "Failed";
        toolVm.NotifyToolStatusChanged();

        Assert.Equal(StrataTheme.Controls.StrataAiToolCallStatus.Failed, terminal.Status);
        Assert.Equal("Command failed with exit code 1.", terminal.Output);
    }

    [Fact]
    public void ProcessMessageToTranscript_SequentialFastTools_KeepOpenGroupMounted()
    {
        var builder = CreateBuilder();
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        var firstTool = CreateToolVm("tool-1", "view", "InProgress", "{\"path\":\"E:\\\\repo\\\\notes.txt\"}");
        builder.ProcessMessageToTranscript(firstTool);

        var turn = Assert.Single(liveTurns);
        var group = Assert.IsType<ToolGroupItem>(Assert.Single(turn.Items));

        firstTool.Message.ToolStatus = "Completed";
        firstTool.NotifyToolStatusChanged();

        Assert.Same(group, Assert.Single(turn.Items));
        Assert.False(group.IsActive);
        Assert.Single(group.ToolCalls);

        var secondTool = CreateToolVm("tool-2", "powershell", "InProgress", "{\"command\":\"dotnet test\"}");
        builder.ProcessMessageToTranscript(secondTool);

        Assert.Same(group, Assert.Single(turn.Items));
        Assert.True(group.IsActive);
        Assert.Equal(2, group.ToolCalls.Count);

        secondTool.Message.ToolStatus = "Completed";
        secondTool.NotifyToolStatusChanged();

        Assert.Same(group, Assert.Single(turn.Items));
        Assert.False(group.IsActive);
        Assert.Equal(2, group.ToolCalls.Count);
    }

    [Fact]
    public void CollapseCompletedBlocksInCurrentTurn_CollapsesToolOnlyTurnBeforeIdle()
    {
        var builder = CreateBuilder();
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        builder.ProcessMessageToTranscript(CreateToolVm("tool-1", "view", "Completed", "{\"path\":\"E:\\\\repo\\\\README.md\"}"));
        builder.ProcessMessageToTranscript(CreateReasoningVm("Checking the folder layout directly."));
        builder.ProcessMessageToTranscript(CreateToolVm("tool-2", "powershell", "Completed", "{\"command\":\"dotnet test\"}"));
        builder.ProcessMessageToTranscript(CreateReasoningVm("Verifying the result."));

        builder.CollapseCompletedBlocksInCurrentTurn();

        var turn = Assert.Single(liveTurns);
        var summary = Assert.IsType<TurnSummaryItem>(Assert.Single(turn.Items));
        Assert.Equal(4, summary.InnerItems.Count);
        Assert.IsType<SingleToolItem>(summary.InnerItems[0]);
        Assert.IsType<ReasoningItem>(summary.InnerItems[1]);
        Assert.IsType<SingleToolItem>(summary.InnerItems[2]);
        Assert.IsType<ReasoningItem>(summary.InnerItems[3]);
    }

    [Fact]
    public void CloseCurrentToolGroup_PreservesIncompleteGroupActivityBeforeIdle()
    {
        var builder = CreateBuilder();
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        builder.ProcessMessageToTranscript(CreateToolVm("tool-1", "view", "Completed", "{\"path\":\"E:\\\\repo\\\\README.md\"}"));
        builder.ProcessMessageToTranscript(CreateToolVm("tool-2", "powershell", "InProgress", "{\"command\":\"dotnet test\"}"));

        var group = Assert.IsType<ToolGroupItem>(Assert.Single(Assert.Single(liveTurns).Items));
        Assert.True(group.IsActive);
        Assert.Single(group.ActivityPreview);

        builder.CloseCurrentToolGroup();
        builder.CollapseCompletedBlocksInCurrentTurn();

        group = Assert.IsType<ToolGroupItem>(Assert.Single(Assert.Single(liveTurns).Items));
        Assert.True(group.IsActive);
        Assert.False(group.IsExpanded);
        Assert.Single(group.ActivityPreview);
    }

    [Fact]
    public void Rebuild_PreservesInProgressToolGroupActivity()
    {
        var builder = CreateBuilder();

        var turns = builder.Rebuild(
        [
            CreateToolVm("tool-1", "view", "Completed", "{\"path\":\"E:\\\\repo\\\\README.md\"}"),
            CreateToolVm("tool-2", "powershell", "InProgress", "{\"command\":\"dotnet test\"}"),
        ]);

        var turn = Assert.Single(turns);
        var group = Assert.IsType<ToolGroupItem>(Assert.Single(turn.Items));
        Assert.True(group.IsActive);
        Assert.Equal(2, group.ToolCalls.Count);
        Assert.Equal("dotnet test", Assert.Single(group.ActivityPreview).Detail);
        Assert.Equal(50, group.ProgressValue);
        Assert.Equal(StrataTheme.Controls.StrataAiToolCallStatus.Completed, Assert.IsType<ToolCallItem>(group.ToolCalls[0]).Status);
        Assert.Equal(StrataTheme.Controls.StrataAiToolCallStatus.InProgress, Assert.IsType<TerminalPreviewItem>(group.ToolCalls[1]).Status);
    }

    [Fact]
    public void Rebuild_FailedGenericToolShowsPersistedErrorOutput()
    {
        var builder = CreateBuilder();

        var failedTool = CreateToolVm(
            "tool-1",
            "example_mcp_lookup",
            "Failed",
            "{\"query\":\"Busy\"}");
        failedTool.Message.ToolOutput = "MCP server returned an example lookup failure.";

        var turns = builder.Rebuild([failedTool]);

        var turn = Assert.Single(turns);
        var singleTool = Assert.IsType<SingleToolItem>(Assert.Single(turn.Items));
        var toolCall = Assert.IsType<ToolCallItem>(singleTool.Inner);
        Assert.Equal(StrataTheme.Controls.StrataAiToolCallStatus.Failed, toolCall.Status);
        Assert.Contains("MCP server returned an example lookup failure.", toolCall.MoreInfo);
    }

    [Fact]
    public void Rebuild_FailedToolWithFriendlyInfoStillShowsPersistedErrorOutput()
    {
        var builder = CreateBuilder();
        var failedTool = CreateToolVm(
            "tool-1",
            "web_fetch",
            "Failed",
            "{\"url\":\"https://example.com/docs\"}");
        failedTool.Message.ToolOutput = "Request failed with status 500.";

        var turns = builder.Rebuild([failedTool]);

        var turn = Assert.Single(turns);
        var singleTool = Assert.IsType<SingleToolItem>(Assert.Single(turn.Items));
        var toolCall = Assert.IsType<ToolCallItem>(singleTool.Inner);
        Assert.Equal(StrataTheme.Controls.StrataAiToolCallStatus.Failed, toolCall.Status);
        Assert.Contains("example.com", toolCall.MoreInfo);
        Assert.Contains("Request failed with status 500.", toolCall.MoreInfo);
    }

    [Fact]
    public void ProcessMessageToTranscript_NonStreamingAssistant_CollapsesPriorActivityImmediately()
    {
        var builder = CreateBuilder();
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        builder.ProcessMessageToTranscript(CreateToolVm("tool-1", "view", "Completed", "{\"path\":\"E:\\\\repo\\\\README.md\"}"));
        builder.ProcessMessageToTranscript(CreateReasoningVm("Checking the folder layout directly."));
        builder.ProcessMessageToTranscript(CreateToolVm("tool-2", "powershell", "Completed", "{\"command\":\"dotnet test\"}"));
        builder.ProcessMessageToTranscript(CreateAssistantVm("Done."));

        var turn = Assert.Single(liveTurns);
        Assert.Equal(2, turn.Items.Count);
        var summary = Assert.IsType<TurnSummaryItem>(turn.Items[0]);
        Assert.IsType<AssistantMessageItem>(turn.Items[1]);
        Assert.Equal(3, summary.InnerItems.Count);
    }

    [Fact]
    public void Rebuild_CollapsesCompletedToolOnlyTurn()
    {
        var builder = CreateBuilder();

        var turns = builder.Rebuild(
        [
            CreateToolVm("tool-1", "view", "Completed", "{\"path\":\"E:\\\\repo\\\\README.md\"}"),
            CreateReasoningVm("Checking the folder layout directly."),
            CreateToolVm("tool-2", "powershell", "Completed", "{\"command\":\"dotnet test\"}"),
        ]);

        var turn = Assert.Single(turns);
        var summary = Assert.IsType<TurnSummaryItem>(Assert.Single(turn.Items));
        Assert.Equal(3, summary.InnerItems.Count);
        Assert.IsType<SingleToolItem>(summary.InnerItems[0]);
        Assert.IsType<ReasoningItem>(summary.InnerItems[1]);
        Assert.IsType<SingleToolItem>(summary.InnerItems[2]);
    }

    [Fact]
    public void Rebuild_CollapsesCompletedBlocksThatAppearAfterAssistantMessage()
    {
        var builder = CreateBuilder();
        var turns = builder.Rebuild(
        [
            CreateAssistantVm("The first README path guess was wrong."),
            CreateToolVm("tool-1", "view", "Completed", "{\"path\":\"E:\\\\repo\\\\README.md\"}"),
            CreateReasoningVm("Checking the folder layout directly.")
        ]);

        var turn = Assert.Single(turns);
        Assert.Equal(2, turn.Items.Count);
        Assert.IsType<AssistantMessageItem>(turn.Items[0]);

        var summary = Assert.IsType<TurnSummaryItem>(turn.Items[1]);
        Assert.Equal(2, summary.InnerItems.Count);
        Assert.IsType<SingleToolItem>(summary.InnerItems[0]);
        Assert.IsType<ReasoningItem>(summary.InnerItems[1]);
    }

    [Fact]
    public void CollapseCompletedBlocksInCurrentTurn_CompactsTailBlocksAfterAssistant()
    {
        var builder = CreateBuilder();
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        builder.ProcessMessageToTranscript(CreateAssistantVm("The first README path guess was wrong."));
        builder.ProcessMessageToTranscript(CreateToolVm("tool-1", "view", "Completed", "{\"path\":\"E:\\\\repo\\\\README.md\"}"));
        builder.ProcessMessageToTranscript(CreateReasoningVm("Checking the folder layout directly."));

        builder.CollapseCompletedBlocksInCurrentTurn();

        var turn = Assert.Single(liveTurns);
        Assert.Equal(2, turn.Items.Count);
        Assert.IsType<AssistantMessageItem>(turn.Items[0]);
        var summary = Assert.IsType<TurnSummaryItem>(turn.Items[1]);
        Assert.Equal(2, summary.InnerItems.Count);
    }

    [Fact]
    public void ProcessMessageToTranscript_StreamingAssistantEndKeepsPriorActivityBetweenAssistantMessages()
    {
        var builder = CreateBuilder();
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        builder.ProcessMessageToTranscript(CreateAssistantVm("I will inspect the file."));
        builder.ProcessMessageToTranscript(CreateToolVm("tool-1", "view", "Completed", "{\"path\":\"E:\\\\repo\\\\README.md\"}"));
        builder.ProcessMessageToTranscript(CreateReasoningVm("The first file is not enough context."));
        var streamingAssistant = CreateAssistantVm("I need to check one more thing.", isStreaming: true);
        builder.ProcessMessageToTranscript(streamingAssistant);

        streamingAssistant.Message.IsStreaming = false;
        streamingAssistant.NotifyStreamingEnded();

        var turn = Assert.Single(liveTurns);
        Assert.Equal(3, turn.Items.Count);
        Assert.IsType<AssistantMessageItem>(turn.Items[0]);

        var summary = Assert.IsType<TurnSummaryItem>(turn.Items[1]);
        Assert.Equal(2, summary.InnerItems.Count);
        Assert.IsType<SingleToolItem>(summary.InnerItems[0]);
        Assert.IsType<ReasoningItem>(summary.InnerItems[1]);
        Assert.IsType<AssistantMessageItem>(turn.Items[2]);
    }

    [Fact]
    public void CollapseCompletedBlocksInCurrentTurn_MergesPriorTailSummaryWithLaterTools()
    {
        var builder = CreateBuilder();
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        builder.ProcessMessageToTranscript(CreateAssistantVm("I will inspect the file."));
        builder.ProcessMessageToTranscript(CreateToolVm("tool-1", "view", "Completed", "{\"path\":\"E:\\\\repo\\\\README.md\"}"));
        builder.ProcessMessageToTranscript(CreateReasoningVm("The first file is not enough context."));
        builder.ProcessMessageToTranscript(CreateAssistantVm("I need to check one more thing."));
        builder.CollapseCompletedBlocksInCurrentTurn();

        builder.ProcessMessageToTranscript(CreateToolVm("tool-2", "powershell", "Completed", "{\"command\":\"dotnet test\"}"));
        builder.ProcessMessageToTranscript(CreateAssistantVm("Now I have the final result."));
        builder.CollapseCompletedBlocksInCurrentTurn();

        var turn = Assert.Single(liveTurns);
        Assert.Equal(5, turn.Items.Count);
        Assert.IsType<AssistantMessageItem>(turn.Items[0]);

        var summary = Assert.IsType<TurnSummaryItem>(turn.Items[1]);
        Assert.Equal(2, summary.InnerItems.Count);
        Assert.IsType<SingleToolItem>(summary.InnerItems[0]);
        Assert.IsType<ReasoningItem>(summary.InnerItems[1]);
        Assert.IsType<AssistantMessageItem>(turn.Items[2]);
        Assert.IsType<SingleToolItem>(turn.Items[3]);
        Assert.IsType<AssistantMessageItem>(turn.Items[4]);
    }

    [Fact]
    public void CollapseCompletedBlocksInCurrentTurn_KeepsMultipleToolGroupsCompactBetweenAssistantMessages()
    {
        var builder = CreateBuilder();
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        builder.ProcessMessageToTranscript(CreateAssistantVm("I'll inspect the first area."));
        builder.ProcessMessageToTranscript(CreateToolVm("tool-1", "view", "Completed", "{\"path\":\"E:\\\\repo\\\\README.md\"}"));
        builder.ProcessMessageToTranscript(CreateToolVm("tool-2", "powershell", "Completed", "{\"command\":\"dotnet build\"}"));
        builder.ProcessMessageToTranscript(CreateAssistantVm("The first check is done; I'll inspect another area."));
        builder.ProcessMessageToTranscript(CreateReasoningVm("The second area needs a search and a file read."));
        builder.ProcessMessageToTranscript(CreateToolVm("tool-3", "rg", "Completed", "{\"pattern\":\"ToolGroup\"}"));
        builder.ProcessMessageToTranscript(CreateToolVm("tool-4", "view", "Completed", "{\"path\":\"E:\\\\repo\\\\src\\\\Lumi\\\\ViewModels\\\\TranscriptBuilder.cs\"}"));
        builder.ProcessMessageToTranscript(CreateAssistantVm("Second check is done; one final command remains."));
        builder.ProcessMessageToTranscript(CreateToolVm("tool-5", "powershell", "Completed", "{\"command\":\"dotnet test\"}"));
        builder.ProcessMessageToTranscript(CreateToolVm("tool-6", "powershell", "Completed", "{\"command\":\"git status\"}"));

        builder.CloseCurrentToolGroup();
        builder.CollapseCompletedBlocksInCurrentTurn();

        var turn = Assert.Single(liveTurns);
        Assert.Equal(6, turn.Items.Count);
        Assert.IsType<AssistantMessageItem>(turn.Items[0]);
        AssertCompactFinishedToolGroup(turn.Items[1], expectedToolCalls: 2);
        Assert.IsType<AssistantMessageItem>(turn.Items[2]);

        var middleSummary = Assert.IsType<TurnSummaryItem>(turn.Items[3]);
        Assert.False(middleSummary.IsExpanded);
        Assert.Equal(2, middleSummary.InnerItems.Count);
        Assert.IsType<ReasoningItem>(middleSummary.InnerItems[0]);
        AssertCompactFinishedToolGroup(middleSummary.InnerItems[1], expectedToolCalls: 2);
        Assert.IsType<AssistantMessageItem>(turn.Items[4]);
        AssertCompactFinishedToolGroup(turn.Items[5], expectedToolCalls: 2);
    }

    [Fact]
    public void ProcessMessageToTranscript_FileEditToolTracksChangesWhenToolCallsHidden()
    {
        var builder = CreateBuilder(showToolCalls: false);
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        builder.ProcessMessageToTranscript(CreateToolVm(
            "tool-1",
            "edit",
            "Completed",
            "{\"filePath\":\"E:\\\\repo\\\\Widget.cs\",\"oldString\":\"old\",\"newString\":\"new\"}"));
        builder.FlushPendingFileEdits();

        var turn = Assert.Single(liveTurns);
        var summary = Assert.IsType<FileChangesSummaryItem>(Assert.Single(turn.Items));
        var change = Assert.Single(summary.FileChanges);
        Assert.Equal("Widget.cs", change.FileName);
        Assert.Equal(1, change.LinesAdded);
        Assert.Equal(1, change.LinesRemoved);
    }

    [Fact]
    public void ProcessMessageToTranscript_WorkspaceFileChangedToolFlushesCreatedFileSummary()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"lumi-transcript-{Guid.NewGuid():N}.txt");
        File.WriteAllText(filePath, "one" + Environment.NewLine + "two");

        try
        {
            var builder = CreateBuilder();
            var liveTurns = new ObservableCollection<TranscriptTurn>();
            builder.SetLiveTarget(liveTurns);

            builder.ProcessMessageToTranscript(CreateToolVm(
                "workspace-file-1",
                ToolDisplayHelper.WorkspaceFileChangedToolName,
                "Completed",
                CreateWorkspaceFileChangedJson(filePath, "Create")));
            builder.FlushPendingFileEdits();

            var turn = Assert.Single(liveTurns);
            var summary = Assert.IsType<FileChangesSummaryItem>(Assert.Single(turn.Items));
            var change = Assert.Single(summary.FileChanges);
            Assert.Equal(filePath, change.FilePath);
            Assert.True(change.IsCreate);
            Assert.True(change.HasSnapshots);
            Assert.Equal(2, change.LinesAdded);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void ProcessMessageToTranscript_LateWorkspaceFileChangedAfterIdleFlushesSummary()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"lumi-transcript-{Guid.NewGuid():N}.txt");
        var secondFilePath = Path.Combine(Path.GetTempPath(), $"lumi-transcript-{Guid.NewGuid():N}.txt");
        File.WriteAllText(filePath, "after idle");
        File.WriteAllText(secondFilePath, "also after idle");

        try
        {
            var builder = CreateBuilder();
            var liveTurns = new ObservableCollection<TranscriptTurn>();
            builder.SetLiveTarget(liveTurns);

            builder.ProcessMessageToTranscript(CreateAssistantVm("Done."));
            builder.AppendModelLabel("gpt-5.5");
            builder.FlushPendingFileEdits();

            builder.ProcessMessageToTranscript(CreateToolVm(
                "workspace-file-1",
                ToolDisplayHelper.WorkspaceFileChangedToolName,
                "Completed",
                CreateWorkspaceFileChangedJson(filePath, "Modify")));
            builder.ProcessMessageToTranscript(CreateToolVm(
                "workspace-file-2",
                ToolDisplayHelper.WorkspaceFileChangedToolName,
                "Completed",
                CreateWorkspaceFileChangedJson(secondFilePath, "Modify")));

            var turn = Assert.Single(liveTurns);
            var summary = Assert.IsType<FileChangesSummaryItem>(Assert.Single(turn.Items.OfType<FileChangesSummaryItem>()));
            Assert.Equal(2, summary.FileChanges.Count);
            Assert.Contains(summary.FileChanges,
                change => string.Equals(change.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(summary.FileChanges,
                change => string.Equals(change.FilePath, secondFilePath, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(filePath);
            File.Delete(secondFilePath);
        }
    }

    [Fact]
    public void ProcessMessageToTranscript_ApplyPatchToolTracksChangedFiles()
    {
        var builder = CreateBuilder(showToolCalls: false);
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        builder.ProcessMessageToTranscript(CreateToolVm(
            "apply-patch-1",
            "apply_patch",
            "Completed",
            """
            *** Begin Patch
            *** Update File: src\Lumi\ViewModels\Widget.cs
            @@
            -old
            +new
            *** Add File: src\Lumi\NewFile.cs
            +line one
            +line two
            *** End Patch
            """));
        builder.FlushPendingFileEdits();

        var turn = Assert.Single(liveTurns);
        var summary = Assert.IsType<FileChangesSummaryItem>(Assert.Single(turn.Items));
        Assert.Equal(2, summary.FileChanges.Count);
        Assert.Contains(summary.FileChanges,
            change => change.FilePath.EndsWith(@"Widget.cs", StringComparison.Ordinal) && !change.IsCreate);
        Assert.Contains(summary.FileChanges,
            change => change.FilePath.EndsWith(@"NewFile.cs", StringComparison.Ordinal) && change.IsCreate);
        Assert.Equal("+3", summary.TotalStatsAdded);
        Assert.Equal("−1", summary.TotalStatsRemoved);
    }

    [Fact]
    public void ProcessMessageToTranscript_LateApplyPatchToolFlushesSummaryAfterIdle()
    {
        var builder = CreateBuilder();
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        builder.ProcessMessageToTranscript(CreateAssistantVm("Done."));
        builder.AppendModelLabel("gpt-5.5");
        builder.FlushPendingFileEdits();

        builder.ProcessMessageToTranscript(CreateToolVm(
            "apply-patch-late",
            "apply_patch",
            "Completed",
            """
            *** Begin Patch
            *** Add File: file-change-proof.txt
            +proof
            *** End Patch
            """));

        var turn = Assert.Single(liveTurns);
        var summary = Assert.IsType<FileChangesSummaryItem>(Assert.Single(turn.Items.OfType<FileChangesSummaryItem>()));
        var change = Assert.Single(summary.FileChanges);
        Assert.Equal("file-change-proof.txt", change.FilePath);
        Assert.True(change.IsCreate);
    }

    [Fact]
    public void ProcessMessageToTranscript_FileEditToolTracksDiffWhenArgsArriveAfterToolStart()
    {
        var builder = CreateBuilder();
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        var toolVm = CreateToolVm("apply-patch-deferred", "apply_patch", "InProgress", "");
        builder.ProcessMessageToTranscript(toolVm);
        builder.ProcessMessageToTranscript(CreateAssistantVm("Done."));
        builder.AppendModelLabel("gpt-5.5");
        builder.FlushPendingFileEdits();

        toolVm.Message.Content = """
            *** Begin Patch
            *** Add File: deferred-file-change.txt
            +proof
            *** End Patch
            """;
        toolVm.NotifyContentChanged();
        toolVm.Message.ToolStatus = "Completed";
        toolVm.NotifyToolStatusChanged();

        var turn = Assert.Single(liveTurns);
        var summary = Assert.IsType<FileChangesSummaryItem>(Assert.Single(turn.Items.OfType<FileChangesSummaryItem>()));
        var change = Assert.Single(summary.FileChanges);
        Assert.Equal("deferred-file-change.txt", change.FilePath);
    }

    [Fact]
    public void ProcessMessageToTranscript_FailedFileEditToolRemovesPendingDiffs()
    {
        var builder = CreateBuilder();
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        var toolVm = CreateToolVm(
            "apply-patch-failed",
            "apply_patch",
            "InProgress",
            """
            *** Begin Patch
            *** Add File: failed-file-change.txt
            +proof
            *** End Patch
            """);

        builder.ProcessMessageToTranscript(toolVm);
        toolVm.Message.ToolStatus = "Failed";
        toolVm.NotifyToolStatusChanged();
        builder.FlushPendingFileEdits();

        Assert.Empty(builder.PendingFileEdits);
        Assert.DoesNotContain(
            Assert.Single(liveTurns).Items,
            item => item is FileChangesSummaryItem);
    }

    [Fact]
    public void ProcessMessageToTranscript_FailedDeferredFileEditToolRemovesPendingDiffs()
    {
        var builder = CreateBuilder();
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        var toolVm = CreateToolVm("apply-patch-deferred-failed", "apply_patch", "InProgress", "");
        builder.ProcessMessageToTranscript(toolVm);

        toolVm.Message.Content = """
            *** Begin Patch
            *** Add File: failed-deferred-file-change.txt
            +proof
            *** End Patch
            """;
        toolVm.NotifyContentChanged();
        toolVm.Message.ToolStatus = "Failed";
        toolVm.NotifyToolStatusChanged();
        builder.FlushPendingFileEdits();

        Assert.Empty(builder.PendingFileEdits);
        Assert.DoesNotContain(
            Assert.Single(liveTurns).Items,
            item => item is FileChangesSummaryItem);
    }

    [Fact]
    public void ProcessMessageToTranscript_HiddenFailedFileEditToolRemovesPendingDiffs()
    {
        var builder = CreateBuilder(showToolCalls: false);
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        var toolVm = CreateToolVm(
            "apply-patch-hidden-failed",
            "apply_patch",
            "InProgress",
            """
            *** Begin Patch
            *** Add File: hidden-failed-file-change.txt
            +proof
            *** End Patch
            """);

        builder.ProcessMessageToTranscript(toolVm);
        toolVm.Message.ToolStatus = "Failed";
        toolVm.NotifyToolStatusChanged();
        builder.FlushPendingFileEdits();

        Assert.Empty(builder.PendingFileEdits);
        Assert.Empty(liveTurns);
    }

    [Fact]
    public void FlushPendingFileEdits_MergesLaterChangesForSameFile()
    {
        var builder = CreateBuilder(showToolCalls: false);
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        builder.ProcessMessageToTranscript(CreateToolVm(
            "edit-1",
            "edit",
            "Completed",
            "{\"filePath\":\"E:\\\\repo\\\\Widget.cs\",\"oldString\":\"old\",\"newString\":\"new\"}"));
        builder.FlushPendingFileEdits();

        builder.ProcessMessageToTranscript(CreateToolVm(
            "edit-2",
            "edit",
            "Completed",
            "{\"filePath\":\"E:\\\\repo\\\\Widget.cs\",\"oldString\":\"new\",\"newString\":\"newer\"}"));
        builder.FlushPendingFileEdits();

        var turn = Assert.Single(liveTurns);
        var summary = Assert.IsType<FileChangesSummaryItem>(Assert.Single(turn.Items));
        var change = Assert.Single(summary.FileChanges);
        Assert.Equal("Widget.cs", change.FileName);
        Assert.Equal(2, change.Edits.Count);
        Assert.Equal("+2", summary.TotalStatsAdded);
        Assert.Equal("−2", summary.TotalStatsRemoved);
    }

    [Fact]
    public void ProcessMessageToTranscript_FetchSkillTool_ShowsInlineSkillChipMidTurn()
    {
        var builder = CreateBuilder();
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        var fetchSkill = CreateToolVm("tool-1", "fetch_skill", "Completed", "{\"name\":\"Code Helper\"}");
        builder.ProcessMessageToTranscript(fetchSkill);

        var turn = Assert.Single(liveTurns);
        var loaded = Assert.IsType<SkillLoadedItem>(Assert.Single(turn.Items));
        Assert.Equal("Code Helper", loaded.Chip.Name);
    }

    [Fact]
    public void ProcessMessageToTranscript_FetchSkillTwice_ShowsInlineChipOnce()
    {
        var builder = CreateBuilder();
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        builder.ProcessMessageToTranscript(CreateToolVm("tool-1", "fetch_skill", "Completed", "{\"name\":\"Code Helper\"}"));
        builder.ProcessMessageToTranscript(CreateToolVm("tool-2", "fetch_skill", "Completed", "{\"name\":\"Code Helper\"}"));

        var chips = liveTurns.SelectMany(t => t.Items).OfType<SkillLoadedItem>().ToList();
        Assert.Single(chips);
    }

    [Fact]
    public void ProcessMessageToTranscript_AssistantSkillFromSdk_ShowsChipOnAssistantMessage()
    {
        var builder = CreateBuilder();
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        var assistant = CreateAssistantVm("Done.");
        assistant.Message.ActiveSkills.Add(new SkillReference { Name = "Document Creator" });
        builder.ProcessMessageToTranscript(assistant);

        var item = liveTurns.SelectMany(t => t.Items).OfType<AssistantMessageItem>().Single();
        Assert.True(item.HasSkills);
        Assert.Contains(item.SkillChips, c => c.Name == "Document Creator");
    }

    [Fact]
    public void ProcessMessageToTranscript_FetchSkillUnknownName_ShowsNoChip()
    {
        var builder = CreateBuilder();
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        // "No Such Skill" is not in the data store and there is no external resolver,
        // so a not-found fetch_skill must not leave behind a misleading chip.
        builder.ProcessMessageToTranscript(CreateToolVm("tool-1", "fetch_skill", "Completed", "{\"name\":\"No Such Skill\"}"));

        Assert.DoesNotContain(liveTurns.SelectMany(t => t.Items), i => i is SkillLoadedItem);
    }

    [Fact]
    public void ProcessMessageToTranscript_ManageChatsLinkedChat_ShowsInlineOpenChatChip()
    {
        Guid? opened = null;
        var builder = CreateBuilder(openChatAction: id => opened = id);
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        var manageChats = CreateToolVm("tool-1", "manage_chats", "Completed", "{\"action\":\"create\",\"title\":\"Chip Worker\"}");
        builder.ProcessMessageToTranscript(manageChats);

        // The linked chat id/title is stamped asynchronously after the orchestration tool runs.
        var linkedChatId = Guid.NewGuid();
        manageChats.Message.LinkedChatId = linkedChatId;
        manageChats.Message.LinkedChatTitle = "Chip Worker";
        manageChats.NotifyLinkedChatChanged();

        var chip = liveTurns.SelectMany(t => t.Items).OfType<LinkedChatItem>().Single();
        Assert.Equal(linkedChatId, chip.Chip.ChatId);
        Assert.Equal("Chip Worker", chip.Chip.Title);

        chip.Chip.OpenCommand.Execute(null);
        Assert.Equal(linkedChatId, opened);
    }

    [Fact]
    public void Rebuild_ManageChatsLinkedChat_ShowsInlineOpenChatChip()
    {
        var builder = CreateBuilder();
        var linkedChatId = Guid.NewGuid();

        var manageChats = CreateToolVm("tool-1", "manage_chats", "Completed", "{\"action\":\"create\",\"title\":\"Chip Worker\"}");
        manageChats.Message.LinkedChatId = linkedChatId;
        manageChats.Message.LinkedChatTitle = "Chip Worker";

        var turns = builder.Rebuild([manageChats]);

        var chip = turns.SelectMany(t => t.Items).OfType<LinkedChatItem>().Single();
        Assert.Equal(linkedChatId, chip.Chip.ChatId);
        Assert.Equal("Chip Worker", chip.Chip.Title);
    }

    [Fact]
    public void ProcessMessageToTranscript_ManageChatsLinkedChatChangedTwice_ShowsChipOnce()
    {
        var builder = CreateBuilder();
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        var manageChats = CreateToolVm("tool-1", "manage_chats", "Completed", "{\"action\":\"create\",\"title\":\"Chip Worker\"}");
        builder.ProcessMessageToTranscript(manageChats);

        manageChats.Message.LinkedChatId = Guid.NewGuid();
        manageChats.Message.LinkedChatTitle = "Chip Worker";
        manageChats.NotifyLinkedChatChanged();

        // A second stamp for the same tool call must not add a duplicate chip.
        manageChats.Message.LinkedChatId = Guid.NewGuid();
        manageChats.Message.LinkedChatTitle = "Chip Worker (renamed)";
        manageChats.NotifyLinkedChatChanged();

        Assert.Single(liveTurns.SelectMany(t => t.Items).OfType<LinkedChatItem>());
    }

    [Fact]
    public void ProcessMessageToTranscript_ManageChatsLinkedChat_ShowsChipEvenWhenToolCallsHidden()
    {
        Guid? opened = null;
        var builder = CreateBuilder(showToolCalls: false, openChatAction: id => opened = id);
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        var manageChats = CreateToolVm("tool-1", "manage_chats", "Completed", "{\"action\":\"create\",\"title\":\"Chip Worker\"}");
        builder.ProcessMessageToTranscript(manageChats);

        // Tool cards are hidden (no tool group), but the open-chat chip is a first-class affordance
        // like a loaded-skill chip and must still surface once the link is stamped asynchronously.
        var linkedChatId = Guid.NewGuid();
        manageChats.Message.LinkedChatId = linkedChatId;
        manageChats.Message.LinkedChatTitle = "Chip Worker";
        manageChats.NotifyLinkedChatChanged();

        var chip = liveTurns.SelectMany(t => t.Items).OfType<LinkedChatItem>().Single();
        Assert.Equal(linkedChatId, chip.Chip.ChatId);
        Assert.Empty(liveTurns.SelectMany(t => t.Items).OfType<ToolGroupItem>());

        chip.Chip.OpenCommand.Execute(null);
        Assert.Equal(linkedChatId, opened);
    }

    [Fact]
    public void ProcessMessageToTranscript_ReadChatLinkedChat_ShowsChipEvenWhenToolCallsHidden()
    {
        Guid? opened = null;
        var builder = CreateBuilder(showToolCalls: false, openChatAction: id => opened = id);
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        var readChat = CreateToolVm("tool-1", "read_chat", "InProgress", "{\"chat\":\"Resolved chat\"}");
        builder.ProcessMessageToTranscript(readChat);

        var linkedChatId = Guid.NewGuid();
        readChat.Message.LinkedChatId = linkedChatId;
        readChat.Message.LinkedChatTitle = "Resolved chat";
        readChat.NotifyLinkedChatChanged();

        var chip = liveTurns.SelectMany(t => t.Items).OfType<LinkedChatItem>().Single();
        Assert.Equal(linkedChatId, chip.Chip.ChatId);
        Assert.Empty(liveTurns.SelectMany(t => t.Items).OfType<ToolGroupItem>());

        chip.Chip.OpenCommand.Execute(null);
        Assert.Equal(linkedChatId, opened);
    }

    [Fact]
    public void Rebuild_ManageChatsLinkedChat_ShowsChipEvenWhenToolCallsHidden()
    {
        var builder = CreateBuilder(showToolCalls: false);
        var linkedChatId = Guid.NewGuid();

        var manageChats = CreateToolVm("tool-1", "manage_chats", "Completed", "{\"action\":\"create\",\"title\":\"Chip Worker\"}");
        manageChats.Message.LinkedChatId = linkedChatId;
        manageChats.Message.LinkedChatTitle = "Chip Worker";

        var turns = builder.Rebuild([manageChats]);

        var chip = turns.SelectMany(t => t.Items).OfType<LinkedChatItem>().Single();
        Assert.Equal(linkedChatId, chip.Chip.ChatId);
        Assert.Empty(turns.SelectMany(t => t.Items).OfType<ToolGroupItem>());
    }

    private static TranscriptBuilder CreateBuilder(bool showToolCalls = true, Action<Guid>? openChatAction = null)
        => new(CreateDataStore(showToolCalls), _ => { }, (_, _) => { }, _ => { }, (_, _) => Task.CompletedTask, () => null,
            openChatAction: openChatAction);

    private static void AssertCompactFinishedToolGroup(TranscriptItem item, int expectedToolCalls)
    {
        var group = Assert.IsType<ToolGroupItem>(item);
        Assert.False(group.IsActive);
        Assert.False(group.IsExpanded);
        Assert.Empty(group.ActivityPreview);
        Assert.Equal(expectedToolCalls, group.ToolCalls.Count);
    }

    private static ChatMessageViewModel CreateToolVm(
        string toolCallId,
        string toolName,
        string toolStatus,
        string content,
        string? parentToolCallId = null)
        => new(new ChatMessage
        {
            Role = "tool",
            ToolCallId = toolCallId,
            ParentToolCallId = parentToolCallId,
            ToolName = toolName,
            ToolStatus = toolStatus,
            Content = content,
            Timestamp = DateTimeOffset.Now,
        });

    private static ChatMessageViewModel CreateAssistantVm(string content, bool isStreaming = false)
        => new(new ChatMessage
        {
            Role = "assistant",
            Content = content,
            Author = "Lumi",
            IsStreaming = isStreaming,
            Timestamp = DateTimeOffset.Now,
        });

    private static ChatMessageViewModel CreateReasoningVm(string content, bool isStreaming = false)
        => new(new ChatMessage
        {
            Role = "reasoning",
            Content = content,
            Author = "Thinking",
            IsStreaming = isStreaming,
            Timestamp = DateTimeOffset.Now,
        });

    private static string CreateWorkspaceFileChangedJson(string filePath, string operation)
        => $"{{\"filePath\":{JsonSerializer.Serialize(filePath)},\"operation\":\"{operation}\"}}";

    private static DataStore CreateDataStore(bool showToolCalls = true)
    {
#pragma warning disable SYSLIB0050
        var store = (DataStore)FormatterServices.GetUninitializedObject(typeof(DataStore));
#pragma warning restore SYSLIB0050
        var data = new AppData();
        data.Settings.ShowToolCalls = showToolCalls;
        data.Skills.Add(new Skill { Name = "Code Helper" });
        typeof(DataStore)
            .GetField("_data", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(store, data);
        return store;
    }
}
