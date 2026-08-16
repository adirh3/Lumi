using System;
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
    public void Rebuild_BuildsRunTimelineFromPromptLogAndToolCalls()
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

        // Prompt → reasoning → tool call → final answer, ordered by when each happened.
        Assert.Collection(
            subagent.Timeline,
            item => Assert.IsType<SubagentPromptItem>(item),
            item => Assert.Equal("thinking", Assert.IsType<SubagentReasoningEntryItem>(item).Text),
            item => Assert.IsType<ToolCallItem>(Assert.IsType<SubagentToolEntryItem>(item).Tool),
            item => Assert.Equal("final answer", Assert.IsType<SubagentAssistantEntryItem>(item).Text));
    }

    [Fact]
    public void Rebuild_WithoutAnExplicitPrompt_OpensTheRunWithTheTaskDescription()
    {
        var builder = CreateBuilder();

        // An `agent:` style call that only carries a task label — no `prompt` field at all.
        var subagent = Assert.IsType<SubagentToolCallItem>(
            Assert.Single(Assert.Single(builder.Rebuild(
            [
                CreateToolVm(
                    "agent-1",
                    "task",
                    "Completed",
                    "{\"description\":\"Benchmark the Sony Bravia 8\",\"agent_type\":\"explore\"}"),
            ])).Items));

        // The run must never open with an empty request.
        Assert.True(subagent.HasPrompt);
        Assert.Equal("Benchmark the Sony Bravia 8", subagent.Prompt);
        Assert.Equal("Benchmark the Sony Bravia 8", subagent.RowTooltip);
        Assert.Equal(
            "Benchmark the Sony Bravia 8",
            Assert.IsType<SubagentPromptItem>(subagent.Timeline[0]).Text);
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
    public void ReasoningEntry_KeepsTheReadersDisclosureStateAcrossLiveUpdates()
    {
        var builder = CreateBuilder();
        builder.SetLiveTarget([]);

        var entries = SubagentRunLog.Append(null, SubagentRunEntryKind.Reasoning, "why", DateTimeOffset.Now);
        builder.ProcessMessageToTranscript(CreateToolVm(
            "agent-1",
            "task",
            "InProgress",
            "{\"description\":\"Inspect repo\",\"agent_type\":\"explore\",\"entries\":" + entries + "}"));

        var run = Assert.Single(builder.SubagentRuns);
        var reasoning = run.Timeline.OfType<SubagentReasoningEntryItem>().Single();

        // Finalized reasoning starts folded, and the reader opens it to inspect.
        Assert.False(reasoning.IsExpanded);
        reasoning.IsExpanded = true;

        // The agent keeps streaming; syncing the timeline must not re-fold what the reader opened,
        // nor disturb the streaming flag the disclosure used to be bound to.
        builder.UpdateSubagentTranscriptText("agent-1", "still working");
        builder.UpdateSubagentTranscriptText("agent-1", "still working more");

        Assert.True(reasoning.IsExpanded);
        Assert.False(reasoning.IsStreaming);
        Assert.Equal("why", reasoning.Text);
    }

    [Fact]
    public void Rebuild_LegacyPayloadWithoutRunLogStillRendersFinalOutput()    {
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
            subagent.Timeline,
            // The task label stands in for the missing prompt so the run still opens with its request.
            item => Assert.Equal("Inspect repo", Assert.IsType<SubagentPromptItem>(item).Text),
            item => Assert.Equal("legacy reasoning", Assert.IsType<SubagentReasoningEntryItem>(item).Text),
            item => Assert.Equal("legacy answer", Assert.IsType<SubagentAssistantEntryItem>(item).Text));
    }

    [Fact]
    public void LiveStreaming_KeepsFinalizedEntriesAndTracksTheStreamingTail()
    {
        var builder = CreateBuilder();
        builder.SetLiveTarget([]);

        var entries = SubagentRunLog.Append(null, SubagentRunEntryKind.Assistant, "first message", DateTimeOffset.Now);
        var root = CreateToolVm(
            "agent-1",
            "task",
            "InProgress",
            "{\"description\":\"Inspect repo\",\"agent_type\":\"explore\",\"entries\":" + entries + "}");
        builder.ProcessMessageToTranscript(root);

        var subagent = Assert.Single(builder.SubagentRuns);
        // The request plus the one finalized message.
        Assert.Equal(2, subagent.Timeline.Count);
        Assert.IsType<SubagentPromptItem>(subagent.Timeline[0]);

        builder.UpdateSubagentTranscriptText("agent-1", "still writ");
        Assert.Equal(3, subagent.Timeline.Count);
        var tail = Assert.IsType<SubagentAssistantEntryItem>(subagent.Timeline[2]);
        Assert.Equal("still writ", tail.Text);
        Assert.True(tail.IsStreaming);

        builder.UpdateSubagentTranscriptText("agent-1", "still writing");
        Assert.Equal(3, subagent.Timeline.Count);
        Assert.Equal("still writing", tail.Text);

        // Clearing the live field (the message finalized into the run log) removes the tail.
        builder.UpdateSubagentTranscriptText("agent-1", "");
        Assert.Equal(2, subagent.Timeline.Count);
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

    private static TranscriptBuilder CreateBuilder(
        Action<SubagentToolCallItem>? openRun = null,
        Action? runsChanged = null)
        => new(
            CreateDataStore(),
            _ => { },
            (_, _) => { },
            _ => { },
            (_, _) => Task.CompletedTask,
            () => null,
            openSubagentRunAction: openRun,
            subagentRunsChanged: runsChanged);

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
