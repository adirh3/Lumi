using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// The sub-agent card is now a compact "chat" entry whose full run opens as a read-only transcript.
/// These cover the data that transcript is built from: the persisted run log, the instruction the
/// agent received, and the ordering of text against the agent's tool calls.
/// </summary>
public sealed class SubagentRunTranscriptTests
{
    [Fact]
    public void RunLog_AppendsOrderedEntriesAndSurvivesRoundTrip()
    {
        var start = new DateTimeOffset(2026, 8, 15, 10, 0, 0, TimeSpan.Zero);

        var json = SubagentRunLog.Append(null, SubagentRunEntryKind.Reasoning, "planning", start);
        json = SubagentRunLog.Append(json, SubagentRunEntryKind.Assistant, "found it", start.AddSeconds(2));

        var entries = SubagentRunLog.Parse(json);

        Assert.Equal(2, entries.Count);
        Assert.Equal(SubagentRunEntryKind.Reasoning, entries[0].Kind);
        Assert.Equal("planning", entries[0].Text);
        Assert.Equal(start, entries[0].Timestamp);
        Assert.Equal(SubagentRunEntryKind.Assistant, entries[1].Kind);
        Assert.Equal("found it", entries[1].Text);
    }

    [Fact]
    public void RunLog_IgnoresBlankAndRepeatedEntries()
    {
        var now = DateTimeOffset.Now;

        var json = SubagentRunLog.Append(null, SubagentRunEntryKind.Assistant, "  ", now);
        Assert.Empty(SubagentRunLog.Parse(json));

        json = SubagentRunLog.Append(json, SubagentRunEntryKind.Reasoning, "same", now);
        // The SDK reports the same reasoning through both the reasoning event and the following
        // assistant message; the duplicate must not double in the run transcript.
        json = SubagentRunLog.Append(json, SubagentRunEntryKind.Reasoning, "same", now.AddSeconds(1));

        Assert.Single(SubagentRunLog.Parse(json));
    }

    [Fact]
    public void RunLog_MalformedPayloadYieldsEmptyLog()
    {
        Assert.Empty(SubagentRunLog.Parse("not json"));
        Assert.Empty(SubagentRunLog.Parse("{\"k\":\"a\"}"));
        Assert.Empty(ToolDisplayHelper.GetSubagentRunEntries("{\"entries\":\"nope\"}"));
    }

    [Fact]
    public void RunTranscript_RendersPromptLogAndToolCallsAsARealTranscript()
    {
        var builder = CreateBuilder();
        var start = DateTimeOffset.Now.AddMinutes(-2);

        var entries = SubagentRunLog.Append(null, SubagentRunEntryKind.Reasoning, "thinking", start.AddSeconds(1));
        entries = SubagentRunLog.Append(entries, SubagentRunEntryKind.Assistant, "final answer", start.AddSeconds(30));

        var payload = "{"
            + "\"description\":\"Inspect repo\",\"agent_type\":\"explore\",\"mode\":\"background\","
            + "\"prompt\":\"Map the transcript pipeline\",\"transcript\":\"\",\"reasoning\":\"\","
            + "\"entries\":" + entries + "}";

        var messages = new[]
        {
            CreateToolVm("agent-1", "task", "Completed", payload, timestamp: start),
            CreateToolVm(
                "child-1",
                "view",
                "Completed",
                "{\"path\":\"E:\\\\repo\\\\README.md\"}",
                parentToolCallId: "agent-1",
                timestamp: start.AddSeconds(10)),
        };

        var subagent = Assert.IsType<SubagentToolCallItem>(
            Assert.Single(Assert.Single(builder.Rebuild(messages)).Items));

        Assert.Equal("Map the transcript pipeline", subagent.Prompt);
        Assert.True(subagent.HasPrompt);

        // Prompt → reasoning → tool call → final answer, ordered by when each happened, and rendered
        // with the chat's own item types rather than a bespoke timeline.
        Assert.Collection(
            RunItems(subagent, messages),
            item => Assert.Equal("Map the transcript pipeline", Assert.IsType<UserMessageItem>(item).Content),
            item => Assert.Equal("thinking", Assert.IsType<ReasoningItem>(item).Content),
            item => Assert.IsType<ToolCallItem>(Assert.IsType<SingleToolItem>(item).Inner),
            item => Assert.Equal("final answer", Assert.IsType<AssistantMessageItem>(item).Content));
    }

    [Fact]
    public void RunTranscript_GroupsConsecutiveToolCallsLikeTheChatDoes()
    {
        var builder = CreateBuilder();
        var start = DateTimeOffset.Now.AddMinutes(-1);

        var messages = new[]
        {
            CreateToolVm(
                "agent-1",
                "task",
                "Completed",
                "{\"description\":\"Inspect repo\",\"agent_type\":\"explore\",\"prompt\":\"Look around\"}",
                timestamp: start),
            CreateToolVm("child-1", "view", "Completed", "{\"path\":\"a.txt\"}",
                parentToolCallId: "agent-1", timestamp: start.AddSeconds(1)),
            CreateToolVm("child-2", "view", "Completed", "{\"path\":\"b.txt\"}",
                parentToolCallId: "agent-1", timestamp: start.AddSeconds(2)),
            CreateToolVm("child-3", "view", "Completed", "{\"path\":\"c.txt\"}",
                parentToolCallId: "agent-1", timestamp: start.AddSeconds(3)),
        };

        var subagent = Assert.IsType<SubagentToolCallItem>(
            Assert.Single(Assert.Single(builder.Rebuild(messages)).Items));

        var items = RunItems(subagent, messages);

        Assert.IsType<UserMessageItem>(items[0]);
        // Three back-to-back steps read as one collapsible group, not three loose cards.
        var group = Assert.IsType<ToolGroupItem>(items[1]);
        Assert.Equal(3, group.ToolCalls.Count);
    }

    [Fact]
    public void RunTranscript_WithoutAnExplicitPrompt_OpensWithTheTaskDescription()
    {
        var builder = CreateBuilder();

        // An `agent:` style call that only carries a task label — no `prompt` field at all.
        var messages = new[]
        {
            CreateToolVm(
                "agent-1",
                "task",
                "Completed",
                "{\"description\":\"Benchmark the Sony Bravia 8\",\"agent_type\":\"explore\"}"),
        };

        var subagent = Assert.IsType<SubagentToolCallItem>(
            Assert.Single(Assert.Single(builder.Rebuild(messages)).Items));

        // The run must never open with an empty request.
        Assert.True(subagent.HasPrompt);
        Assert.Equal("Benchmark the Sony Bravia 8", subagent.Prompt);
        Assert.Equal(
            "Benchmark the Sony Bravia 8",
            Assert.IsType<UserMessageItem>(RunItems(subagent, messages)[0]).Content);
    }

    [Fact]
    public void Rebuild_PrefersTheExplicitPromptOverTheTaskLabel()
    {
        var builder = CreateBuilder();

        var subagent = Assert.IsType<SubagentToolCallItem>(
            Assert.Single(Assert.Single(builder.Rebuild(
            [
                CreateToolVm(
                    "agent-1",
                    "task",
                    "Completed",
                    "{\"description\":\"Short label\",\"agent_type\":\"explore\","
                    + "\"prompt\":\"The full instruction the agent received.\"}"),
            ])).Items));

        Assert.Equal("The full instruction the agent received.", subagent.Prompt);
        Assert.Equal("Short label", subagent.TaskDescription);
    }

    [Fact]
    public void GroupCard_AutoExpansionStopsFightingTheReaderOnceTheyToggleIt()
    {
        var builder = CreateBuilder();
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        builder.ProcessMessageToTranscript(CreateToolVm(
            "agent-1", "task", "InProgress", "{\"description\":\"A\",\"agent_type\":\"research\"}"));
        builder.ProcessMessageToTranscript(CreateToolVm(
            "agent-2", "task", "InProgress", "{\"description\":\"B\",\"agent_type\":\"research\"}"));

        var group = Assert.IsType<SubagentGroupItem>(Assert.Single(Assert.Single(liveTurns).Items));
        Assert.True(group.IsExpanded); // auto-opened while its agents work

        // The reader folds the busy group away.
        group.IsExpanded = false;

        // Its agents keep working — every step recomputes the group's state, and none of it may
        // reopen a card the reader deliberately closed.
        builder.ProcessMessageToTranscript(CreateToolVm(
            "child-1", "view", "Completed", "{\"path\":\"a.txt\"}", parentToolCallId: "agent-1"));
        Assert.False(group.IsExpanded);

        builder.ProcessMessageToTranscript(CreateToolVm(
            "child-2", "powershell", "Completed", "{\"command\":\"dotnet test\"}", parentToolCallId: "agent-2"));
        Assert.False(group.IsExpanded);

        builder.UpdateSubagentToolStatus("agent-1", "Completed");
        Assert.False(group.IsExpanded);
    }

    [Fact]
    public void GroupCard_StaysOpenWhileTheReaderInspectsItAndAgentsFinish()
    {
        var builder = CreateBuilder();
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        builder.ProcessMessageToTranscript(CreateToolVm(
            "agent-1", "task", "InProgress", "{\"description\":\"A\",\"agent_type\":\"research\"}"));
        builder.ProcessMessageToTranscript(CreateToolVm(
            "agent-2", "task", "InProgress", "{\"description\":\"B\",\"agent_type\":\"research\"}"));

        var group = Assert.IsType<SubagentGroupItem>(Assert.Single(Assert.Single(liveTurns).Items));

        // The reader folds it away, then opens it back up to inspect.
        group.IsExpanded = false;
        group.IsExpanded = true;

        // Finishing the batch must not yank the card shut mid-read.
        builder.UpdateSubagentToolStatus("agent-1", "Completed");
        builder.UpdateSubagentToolStatus("agent-2", "Completed");

        Assert.False(group.IsActive);
        Assert.True(group.IsExpanded);
    }

    [Fact]
    public void GroupCard_AutoFoldsWhenFinishedIfTheReaderNeverTouchedIt()
    {
        var builder = CreateBuilder();
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        builder.ProcessMessageToTranscript(CreateToolVm(
            "agent-1", "task", "InProgress", "{\"description\":\"A\",\"agent_type\":\"research\"}"));
        builder.ProcessMessageToTranscript(CreateToolVm(
            "agent-2", "task", "InProgress", "{\"description\":\"B\",\"agent_type\":\"research\"}"));

        var group = Assert.IsType<SubagentGroupItem>(Assert.Single(Assert.Single(liveTurns).Items));
        Assert.True(group.IsExpanded);

        builder.UpdateSubagentToolStatus("agent-1", "Completed");
        builder.UpdateSubagentToolStatus("agent-2", "Completed");

        // Untouched groups still fold away when the work is done.
        Assert.False(group.IsExpanded);
    }

    [Fact]
    public void RunTranscript_KeepsTheReadersDisclosureStateAcrossRebuilds()
    {
        var builder = CreateBuilder();
        var entries = SubagentRunLog.Append(null, SubagentRunEntryKind.Reasoning, "why", DateTimeOffset.Now);
        var messages = new[]
        {
            CreateToolVm(
                "agent-1",
                "task",
                "InProgress",
                "{\"description\":\"Inspect repo\",\"agent_type\":\"explore\",\"entries\":" + entries + "}"),
        };

        var run = Assert.IsType<SubagentToolCallItem>(
            Assert.Single(Assert.Single(builder.Rebuild(messages)).Items));

        var transcript = CreateRunTranscript();
        transcript.Sync(run, messages);
        var reasoning = transcript.Turns.SelectMany(static turn => turn.Items).OfType<ReasoningItem>().Single();

        // Finalized reasoning starts folded, and the reader opens it to inspect.
        Assert.False(reasoning.IsExpanded);
        reasoning.IsExpanded = true;

        // The agent keeps streaming. Every token rebuilds the run, and none of those rebuilds may
        // re-fold a card the reader deliberately opened.
        run.TranscriptText = "still working";
        transcript.Sync(run, messages);
        run.TranscriptText = "still working more";
        transcript.Sync(run, messages);

        var rebuilt = transcript.Turns.SelectMany(static turn => turn.Items).OfType<ReasoningItem>().Single();
        Assert.True(rebuilt.IsExpanded);
        Assert.Equal("why", rebuilt.Content);
    }

    [Fact]
    public void RunTranscript_LegacyPayloadWithoutRunLogStillRendersFinalOutput()
    {
        var builder = CreateBuilder();
        var messages = new[]
        {
            CreateToolVm(
                "agent-1",
                "task",
                "Completed",
                "{\"description\":\"Inspect repo\",\"agent_type\":\"explore\","
                + "\"reasoning\":\"legacy reasoning\",\"transcript\":\"legacy answer\"}"),
        };

        var subagent = Assert.IsType<SubagentToolCallItem>(
            Assert.Single(Assert.Single(builder.Rebuild(messages)).Items));

        Assert.Collection(
            RunItems(subagent, messages),
            // The task label stands in for the missing prompt so the run still opens with its request.
            item => Assert.Equal("Inspect repo", Assert.IsType<UserMessageItem>(item).Content),
            item => Assert.Equal("legacy reasoning", Assert.IsType<ReasoningItem>(item).Content),
            item => Assert.Equal("legacy answer", Assert.IsType<AssistantMessageItem>(item).Content));
    }

    [Fact]
    public void RunTranscript_KeepsFinalizedEntriesAndTracksTheStreamingTail()
    {
        var builder = CreateBuilder();
        var entries = SubagentRunLog.Append(null, SubagentRunEntryKind.Assistant, "first message", DateTimeOffset.Now);
        var messages = new[]
        {
            CreateToolVm(
                "agent-1",
                "task",
                "InProgress",
                "{\"description\":\"Inspect repo\",\"agent_type\":\"explore\",\"entries\":" + entries + "}"),
        };

        var run = Assert.IsType<SubagentToolCallItem>(
            Assert.Single(Assert.Single(builder.Rebuild(messages)).Items));

        var transcript = CreateRunTranscript();
        transcript.Sync(run, messages);

        // The request plus the one finalized message.
        Assert.Equal(2, RunItems(transcript).Count);

        run.TranscriptText = "still writ";
        transcript.Sync(run, messages);
        var items = RunItems(transcript);
        Assert.Equal(3, items.Count);
        Assert.Equal("still writ", Assert.IsType<AssistantMessageItem>(items[2]).Content);

        run.TranscriptText = "still writing";
        transcript.Sync(run, messages);
        items = RunItems(transcript);
        Assert.Equal(3, items.Count);
        Assert.Equal("still writing", Assert.IsType<AssistantMessageItem>(items[2]).Content);

        // Clearing the live field (the message finalized into the run log) removes the tail.
        run.TranscriptText = "";
        transcript.Sync(run, messages);
        Assert.Equal(2, RunItems(transcript).Count);
    }

    [Fact]
    public void Rebuild_RegistersEveryRunForTheChatWideAgentIndex()
    {
        var changeCount = 0;
        var builder = CreateBuilder(runsChanged: () => changeCount++);

        var turns = builder.Rebuild(
        [
            CreateToolVm("agent-1", "task", "Completed", "{\"description\":\"A\",\"agent_type\":\"research\"}"),
            CreateToolVm("agent-2", "task", "InProgress", "{\"description\":\"B\",\"agent_type\":\"explore\"}"),
        ]);

        Assert.Single(turns);
        Assert.Equal(2, builder.SubagentRuns.Count);
        Assert.Equal("A", builder.SubagentRuns[0].Title);
        Assert.Equal("B", builder.SubagentRuns[1].Title);
        Assert.True(changeCount > 0);

        // A rebuild replaces the index rather than appending to it.
        builder.Rebuild([CreateToolVm("agent-3", "task", "Completed", "{\"description\":\"C\",\"agent_type\":\"research\"}")]);
        Assert.Equal("C", Assert.Single(builder.SubagentRuns).Title);
    }

    [Fact]
    public void RunSelection_IsDroppedWhenARebuildNoLongerContainsIt()
    {
        var builder = CreateBuilder();
        builder.Rebuild([CreateToolVm("agent-1", "task", "Completed", "{\"description\":\"A\",\"agent_type\":\"research\"}")]);
        var original = Assert.Single(builder.SubagentRuns);

        // Same run after a rebuild: a different instance carrying the same stable id.
        builder.Rebuild([CreateToolVm("agent-1", "task", "Completed", "{\"description\":\"A\",\"agent_type\":\"research\"}")]);
        var rebuilt = Assert.Single(builder.SubagentRuns);
        Assert.NotSame(original, rebuilt);
        Assert.Equal(original.StableId, rebuilt.StableId);

        // A different chat's transcript has no twin, so a host must not keep the stale selection.
        builder.Rebuild([CreateToolVm("agent-9", "task", "Completed", "{\"description\":\"Z\",\"agent_type\":\"research\"}")]);
        Assert.DoesNotContain(builder.SubagentRuns, run => run.StableId == original.StableId);
    }

    [Fact]
    public void OpenRunCommand_RaisesTheRunOpenAction()
    {
        SubagentToolCallItem? opened = null;
        var builder = CreateBuilder(openRun: run => opened = run);

        builder.Rebuild([CreateToolVm("agent-1", "task", "Completed", "{\"description\":\"A\",\"agent_type\":\"research\"}")]);

        var run = Assert.Single(builder.SubagentRuns);
        run.OpenRunCommand.Execute(null);

        Assert.Same(run, opened);
    }

    [Fact]
    public void RunStableId_SurvivesRebuild_SoAnOpenRunCanBeRePointed()
    {
        var builder = CreateBuilder();
        var messages = new[]
        {
            CreateToolVm("agent-1", "task", "InProgress", "{\"description\":\"A\",\"agent_type\":\"research\"}"),
        };

        builder.Rebuild(messages);
        var first = Assert.Single(builder.SubagentRuns);

        builder.Rebuild(messages);
        var rebuilt = Assert.Single(builder.SubagentRuns);

        Assert.NotSame(first, rebuilt);
        Assert.Equal(first.StableId, rebuilt.StableId);
    }

    [Fact]
    public void LatestActivity_TracksTheMostRecentStep()
    {
        var builder = CreateBuilder();
        builder.Rebuild(
        [
            CreateToolVm("agent-1", "task", "InProgress", "{\"description\":\"A\",\"agent_type\":\"explore\"}"),
            CreateToolVm("child-1", "view", "Completed", "{\"path\":\"a.txt\"}", parentToolCallId: "agent-1"),
            CreateToolVm("child-2", "powershell", "Completed", "{\"command\":\"dotnet test\"}", parentToolCallId: "agent-1"),
        ]);

        var run = Assert.Single(builder.SubagentRuns);
        Assert.Contains("dotnet test", run.LatestActivityText);
        Assert.Equal(2, run.Activities.Count);
    }

    [Fact]
    public void LiveRun_ToolsDoNotOpenAToolGroupBesideTheAgentRow()
    {
        var builder = CreateBuilder();
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        builder.ProcessMessageToTranscript(CreateToolVm(
            "agent-1", "task", "InProgress", "{\"description\":\"Inspect repo\",\"agent_type\":\"explore\"}"));

        // Everything this agent runs belongs to its run. None of it may leave an empty "Working…"
        // tool group stranded under the agent row for as long as the run lasts.
        builder.ProcessMessageToTranscript(CreateToolVm(
            "child-1", "view", "InProgress", "{\"path\":\"a.txt\"}", parentToolCallId: "agent-1"));
        builder.ProcessMessageToTranscript(CreateToolVm(
            "child-2", "powershell", "InProgress", "{\"command\":\"dotnet test\"}", parentToolCallId: "agent-1"));

        var items = liveTurns.SelectMany(static turn => turn.Items).ToList();
        Assert.IsType<SubagentToolCallItem>(Assert.Single(items));

        // Lumi's own next step still opens a group of its own.
        builder.ProcessMessageToTranscript(CreateToolVm("own-1", "view", "InProgress", "{\"path\":\"b.txt\"}"));
        Assert.Single(liveTurns.SelectMany(static turn => turn.Items).OfType<ToolGroupItem>());
    }

    [Fact]
    public void RunTranscript_StreamingTailUpdatesInPlaceWithoutRebuildingTheTree()
    {
        // A streaming agent changes its tail many times a second. Rebuilding for each of those would
        // re-create every item and defeat the incremental transcript machinery, so only a structural
        // change (a new step, a finalized message, a status flip) may rebuild.
        var builder = CreateBuilder();
        var messages = new[]
        {
            CreateToolVm(
                "agent-1",
                "task",
                "InProgress",
                "{\"description\":\"Inspect repo\",\"agent_type\":\"explore\",\"prompt\":\"Look around\"}"),
        };

        var run = Assert.IsType<SubagentToolCallItem>(
            Assert.Single(Assert.Single(builder.Rebuild(messages)).Items));

        var transcript = CreateRunTranscript();
        run.TranscriptText = "Par";
        transcript.Sync(run, messages);

        var tail = RunItems(transcript).OfType<AssistantMessageItem>().Single();
        Assert.Equal("Par", tail.Content);
        var turnsBefore = transcript.Turns;

        // The tail grows: the SAME item instance must track it, and the turn collection must not be
        // swapped out from under the view.
        run.TranscriptText = "Partially don";
        transcript.Sync(run, messages);
        run.TranscriptText = "Partially done";
        transcript.Sync(run, messages);

        Assert.Same(turnsBefore, transcript.Turns);
        Assert.Same(tail, RunItems(transcript).OfType<AssistantMessageItem>().Single());
        Assert.Equal("Partially done", tail.Content);

        // A structural change (the agent ran a tool) does rebuild.
        var withTool = messages.Append(CreateToolVm(
            "child-1", "view", "Completed", "{\"path\":\"a.txt\"}", parentToolCallId: "agent-1")).ToArray();
        transcript.Sync(run, withTool);

        Assert.NotSame(turnsBefore, transcript.Turns);
        Assert.Contains(RunItems(transcript), item => item is SingleToolItem or ToolGroupItem);

        // The replacement item must be re-subscribed and continue taking the in-place fast path.
        var replacementTail = RunItems(transcript).OfType<AssistantMessageItem>().Single();
        Assert.NotSame(tail, replacementTail);
        Assert.True(replacementTail.IsStreaming);
        run.TranscriptText = "Finished after structural rebuild";
        transcript.Sync(run, withTool);
        Assert.Same(replacementTail, RunItems(transcript).OfType<AssistantMessageItem>().Single());
        Assert.Equal("Finished after structural rebuild", replacementTail.Content);
    }

    [Fact]
    public void RunTranscript_DirectRunSwitchDoesNotReuseThePreviousRunsMessages()
    {
        // The main chat remains visible beside the island, so clicking another agent row switches
        // A → B directly without first returning to the index. Both runs deliberately have the same
        // old shape signature: one prompt, one finalized entry, no tools or live tail.
        var entriesA = SubagentRunLog.Append(
            null,
            SubagentRunEntryKind.Reasoning,
            "A is reasoning",
            DateTimeOffset.Now);
        var messagesA = new[]
        {
            CreateToolVm(
                "agent-a",
                "task",
                "Completed",
                "{\"description\":\"Task A\",\"agent_type\":\"explore\",\"prompt\":\"Prompt A\",\"entries\":"
                + entriesA + "}"),
        };
        var builderA = CreateBuilder();
        var runA = Assert.IsType<SubagentToolCallItem>(
            Assert.Single(Assert.Single(builderA.Rebuild(messagesA)).Items));

        var entriesB = SubagentRunLog.Append(
            null,
            SubagentRunEntryKind.Assistant,
            "B answered",
            DateTimeOffset.Now);
        var messagesB = new[]
        {
            CreateToolVm(
                "agent-b",
                "task",
                "Completed",
                "{\"description\":\"Task B\",\"agent_type\":\"research\",\"prompt\":\"Prompt B\",\"entries\":"
                + entriesB + "}"),
        };
        var builderB = CreateBuilder();
        var runB = Assert.IsType<SubagentToolCallItem>(
            Assert.Single(Assert.Single(builderB.Rebuild(messagesB)).Items));

        var transcript = CreateRunTranscript();
        transcript.Sync(runA, messagesA);
        var runATurns = transcript.Turns;
        var runAItems = RunItems(transcript);
        var runAStableId = runAItems[0].StableId;
        Assert.Contains(runAItems, static item => item is ReasoningItem);

        transcript.Sync(runB, messagesB);
        var runBItems = RunItems(transcript);

        Assert.NotSame(runATurns, transcript.Turns);
        Assert.Equal("Prompt B", Assert.IsType<UserMessageItem>(runBItems[0]).Content);
        Assert.DoesNotContain(runBItems, static item => item is ReasoningItem);
        var answer = Assert.IsType<AssistantMessageItem>(runBItems[1]);
        Assert.Equal("B answered", answer.Content);
        Assert.Equal("Research", answer.Author);
        Assert.NotEqual(runAStableId, runBItems[0].StableId);
    }

    [Fact]
    public void RunTranscript_SwitchingFromFinishedToRunningRunKeepsTheTailLive()
    {
        var finishedMessages = new[]
        {
            CreateToolVm(
                "agent-finished",
                "task",
                "Completed",
                "{\"description\":\"Finished\",\"agent_type\":\"explore\",\"prompt\":\"Finished prompt\","
                + "\"transcript\":\"Finished answer\"}"),
        };
        var finishedBuilder = CreateBuilder();
        var finishedRun = Assert.IsType<SubagentToolCallItem>(
            Assert.Single(Assert.Single(finishedBuilder.Rebuild(finishedMessages)).Items));

        var runningMessages = new[]
        {
            CreateToolVm(
                "agent-running",
                "task",
                "InProgress",
                "{\"description\":\"Running\",\"agent_type\":\"research\",\"prompt\":\"Running prompt\","
                + "\"transcript\":\"Starting\"}"),
        };
        var runningBuilder = CreateBuilder();
        var runningRun = Assert.IsType<SubagentToolCallItem>(
            Assert.Single(Assert.Single(runningBuilder.Rebuild(runningMessages)).Items));

        var transcript = CreateRunTranscript();
        transcript.Sync(finishedRun, finishedMessages);
        transcript.Sync(runningRun, runningMessages);

        var tail = RunItems(transcript).OfType<AssistantMessageItem>().Single();
        Assert.True(tail.IsStreaming);
        Assert.Equal("Research", tail.Author);

        runningRun.TranscriptText = "Still working";
        transcript.Sync(runningRun, runningMessages);

        Assert.Same(tail, RunItems(transcript).OfType<AssistantMessageItem>().Single());
        Assert.Equal("Still working", tail.Content);
    }

    [Fact]
    public void RunTranscript_DisplayNameChangeRefreshesCachedMessageMetadata()
    {
        var messages = new[]
        {
            CreateToolVm(
                "agent-1",
                "task",
                "Completed",
                "{\"description\":\"Inspect\",\"agent_type\":\"explore\",\"prompt\":\"Inspect\","
                + "\"transcript\":\"Done\"}"),
        };
        var builder = CreateBuilder();
        var run = Assert.IsType<SubagentToolCallItem>(
            Assert.Single(Assert.Single(builder.Rebuild(messages)).Items));

        var transcript = CreateRunTranscript();
        transcript.Sync(run, messages);
        Assert.Equal("Explore", RunItems(transcript).OfType<AssistantMessageItem>().Single().Author);

        // subagent.started can replace the generic task label with the SDK's real display name.
        run.DisplayName = "Repository Analyst";
        transcript.Sync(run, messages);

        Assert.Equal(
            "Repository Analyst",
            RunItems(transcript).OfType<AssistantMessageItem>().Single().Author);
    }

    [Fact]
    public void RunTranscript_RemovingLiveTailEndsDetachedItemSubscription()
    {
        var messages = new[]
        {
            CreateToolVm(
                "agent-1",
                "task",
                "InProgress",
                "{\"description\":\"Inspect\",\"agent_type\":\"explore\",\"prompt\":\"Inspect\"}"),
        };
        var builder = CreateBuilder();
        var run = Assert.IsType<SubagentToolCallItem>(
            Assert.Single(Assert.Single(builder.Rebuild(messages)).Items));

        var transcript = CreateRunTranscript();
        run.TranscriptText = "Streaming";
        transcript.Sync(run, messages);
        var removedTail = RunItems(transcript).OfType<AssistantMessageItem>().Single();
        Assert.True(removedTail.IsStreaming);

        run.TranscriptText = "";
        transcript.Sync(run, messages);

        Assert.False(removedTail.IsStreaming);
        Assert.Empty(RunItems(transcript).OfType<AssistantMessageItem>());
    }

    [Fact]
    public void RunTranscript_SettingsChangeInvalidatesStructuralCache()
    {
        var dataStore = CreateDataStore();
        var builder = CreateBuilder(dataStore);
        var entries = SubagentRunLog.Append(
            null,
            SubagentRunEntryKind.Reasoning,
            "Private reasoning",
            DateTimeOffset.Now);
        var messages = new[]
        {
            CreateToolVm(
                "agent-1",
                "task",
                "Completed",
                "{\"description\":\"Inspect\",\"agent_type\":\"explore\",\"prompt\":\"Inspect\",\"entries\":"
                + entries + "}"),
            CreateToolVm(
                "child-1",
                "view",
                "Completed",
                "{\"path\":\"a.txt\"}",
                parentToolCallId: "agent-1"),
        };
        var run = Assert.IsType<SubagentToolCallItem>(
            Assert.Single(Assert.Single(builder.Rebuild(messages)).Items));

        var transcript = CreateRunTranscript(dataStore);
        transcript.Sync(run, messages);
        var turnsBefore = transcript.Turns;
        Assert.Contains(RunItems(transcript), static item => item is ReasoningItem);
        Assert.Contains(RunItems(transcript), static item => item is SingleToolItem or ToolGroupItem);

        dataStore.Data.Settings.ShowReasoning = false;
        dataStore.Data.Settings.ShowToolCalls = false;
        transcript.Sync(run, messages);

        Assert.NotSame(turnsBefore, transcript.Turns);
        Assert.DoesNotContain(RunItems(transcript), static item => item is ReasoningItem);
        Assert.DoesNotContain(RunItems(transcript), static item => item is SingleToolItem or ToolGroupItem);
    }

    [Fact]
    public void RunTranscript_ToolPresentationRevisionRebuildsMutableDetails()
    {
        var builder = CreateBuilder();
        var child = CreateToolVm(
            "child-1",
            "powershell",
            "Completed",
            "{\"command\":\"echo hello\"}",
            parentToolCallId: "agent-1");
        child.Message.ToolOutput = "first output";
        var messages = new[]
        {
            CreateToolVm(
                "agent-1",
                "task",
                "Completed",
                "{\"description\":\"Inspect\",\"agent_type\":\"explore\",\"prompt\":\"Inspect\"}"),
            child,
        };
        var run = Assert.IsType<SubagentToolCallItem>(
            Assert.Single(Assert.Single(builder.Rebuild(messages)).Items));

        var transcript = CreateRunTranscript();
        transcript.Sync(run, messages);
        var turnsBefore = transcript.Turns;
        var firstCard = Assert.IsType<TerminalPreviewItem>(
            Assert.IsType<SingleToolItem>(RunItems(transcript).Single(item => item is SingleToolItem)).Inner);
        Assert.Equal("first output", firstCard.Output);

        child.Message.ToolOutput = "final output";
        child.NotifyToolDetailsChanged();
        transcript.Sync(run, messages);

        Assert.NotSame(turnsBefore, transcript.Turns);
        var rebuiltCard = Assert.IsType<TerminalPreviewItem>(
            Assert.IsType<SingleToolItem>(RunItems(transcript).Single(item => item is SingleToolItem)).Inner);
        Assert.Equal("final output", rebuiltCard.Output);
    }

    private static TranscriptBuilder CreateBuilder(
        DataStore? dataStore = null,
        Action<SubagentToolCallItem>? openRun = null,
        Action? runsChanged = null)
        => new(
            dataStore ?? CreateDataStore(),
            _ => { },
            (_, _) => { },
            _ => { },
            (_, _) => Task.CompletedTask,
            () => null,
            openSubagentRunAction: openRun,
            subagentRunsChanged: runsChanged);

    private static SubagentRunTranscript CreateRunTranscript(DataStore? dataStore = null)
        => new(dataStore ?? CreateDataStore(), _ => { });

    /// <summary>Builds one run's island transcript and flattens it to the items it renders.</summary>
    private static IReadOnlyList<TranscriptItem> RunItems(
        SubagentToolCallItem run,
        IReadOnlyList<ChatMessageViewModel> chatMessages)
    {
        var transcript = CreateRunTranscript();
        transcript.Sync(run, chatMessages);
        return RunItems(transcript);
    }

    private static IReadOnlyList<TranscriptItem> RunItems(SubagentRunTranscript transcript)
        => transcript.Turns.SelectMany(static turn => turn.Items).ToList();

    private static ChatMessageViewModel CreateToolVm(
        string toolCallId,
        string toolName,
        string toolStatus,
        string content,
        string? parentToolCallId = null,
        DateTimeOffset? timestamp = null)
        => new(new ChatMessage
        {
            Role = "tool",
            ToolCallId = toolCallId,
            ParentToolCallId = parentToolCallId,
            ToolName = toolName,
            ToolStatus = toolStatus,
            Content = content,
            Timestamp = timestamp ?? DateTimeOffset.Now,
        });

    private static DataStore CreateDataStore()
    {
#pragma warning disable SYSLIB0050
        var store = (DataStore)FormatterServices.GetUninitializedObject(typeof(DataStore));
#pragma warning restore SYSLIB0050
        typeof(DataStore)
            .GetField("_data", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(store, new AppData());
        return store;
    }
}
