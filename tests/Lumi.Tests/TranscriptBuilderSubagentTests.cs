using System;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class TranscriptBuilderSubagentTests
{
    [Fact]
    public void Rebuild_GroupsParallelSubagentsIntoOneGroup()
    {
        var builder = CreateBuilder();
        var messages = new[]
        {
            CreateToolVm("agent-1", "task", "Completed", "{\"description\":\"Explore auth flow\",\"agent_type\":\"explore\",\"mode\":\"sync\"}"),
            CreateToolVm("intent-1", "report_intent", "Completed", "{\"intent\":\"Tracing auth\"}", parentToolCallId: "agent-1"),
            CreateToolVm("child-1", "view", "Completed", "{\"path\":\"E:\\\\repo\\\\README.md\"}", parentToolCallId: "agent-1"),
            CreateToolVm("agent-2", "task", "InProgress", "{\"description\":\"Review UI layout\",\"agent_type\":\"code-review\",\"mode\":\"background\"}"),
            CreateToolVm("child-2", "powershell", "Completed", "{\"command\":\"dotnet test\"}", parentToolCallId: "agent-2"),
        };

        var turns = builder.Rebuild(messages);

        // The two parallel sub-agents collapse into one scannable group instead of stacking.
        var turn = Assert.Single(turns);
        var group = Assert.IsType<SubagentGroupItem>(Assert.Single(turn.Items));
        Assert.Equal(2, group.Count);
        Assert.True(group.IsGrouped);
        Assert.True(group.IsActive); // agent-2 is still running
        Assert.False(group.IsExpanded); // rebuilt chats never reopen running groups automatically
        Assert.False(string.IsNullOrWhiteSpace(group.HeaderLabel));
        Assert.False(string.IsNullOrWhiteSpace(group.Meta));

        var first = group.Subagents[0];
        Assert.Equal("Tracing auth", first.Title);
        Assert.Equal("Explore", first.DisplayName);
        Assert.Same(group, first.OwningGroup);
        Assert.True(first.IsGrouped);
        Assert.False(first.IsExpanded); // grouped members render collapsed
        Assert.IsType<ToolCallItem>(Assert.Single(first.Activities));

        var second = group.Subagents[1];
        Assert.Equal("Review UI layout", second.Title);
        Assert.Equal("Code review", second.DisplayName);
        Assert.Equal("Background", second.ModeLabel);
        Assert.Same(group, second.OwningGroup);
        Assert.False(second.IsExpanded);
        Assert.IsType<TerminalPreviewItem>(Assert.Single(second.Activities));
    }

    [Fact]
    public void Rebuild_LoneSubagentStaysStandaloneCard()
    {
        var builder = CreateBuilder();
        var messages = new[]
        {
            CreateToolVm("agent-1", "task", "Completed", "{\"description\":\"Explore auth flow\",\"agent_type\":\"explore\",\"mode\":\"sync\"}"),
            CreateToolVm("child-1", "view", "Completed", "{\"path\":\"E:\\\\repo\\\\README.md\"}", parentToolCallId: "agent-1"),
        };

        var turns = builder.Rebuild(messages);

        var turn = Assert.Single(turns);
        var lone = Assert.IsType<SubagentToolCallItem>(Assert.Single(turn.Items));
        Assert.Null(lone.OwningGroup);
        Assert.False(lone.IsGrouped);
        Assert.Equal("Explore", lone.DisplayName);
        Assert.False(lone.IsExpanded);
    }

    [Fact]
    public void Rebuild_ThreeParallelSubagents_AggregatesAllIntoGroup()
    {
        var builder = CreateBuilder();
        var messages = new[]
        {
            CreateToolVm("agent-1", "task", "Completed", "{\"description\":\"A\",\"agent_type\":\"research\",\"mode\":\"sync\"}"),
            CreateToolVm("agent-2", "task", "Completed", "{\"description\":\"B\",\"agent_type\":\"research\",\"mode\":\"sync\"}"),
            CreateToolVm("agent-3", "task", "Failed", "{\"description\":\"C\",\"agent_type\":\"explore\",\"mode\":\"sync\"}"),
        };

        var turns = builder.Rebuild(messages);

        var turn = Assert.Single(turns);
        var group = Assert.IsType<SubagentGroupItem>(Assert.Single(turn.Items));
        Assert.Equal(3, group.Count);
        Assert.False(group.IsActive); // none running
        Assert.False(group.IsExpanded);
        Assert.All(group.Subagents, s => Assert.Same(group, s.OwningGroup));
    }

    [Fact]
    public void ProcessMessageToTranscript_SubagentPayloadRefreshesExistingCard()
    {
        var builder = CreateBuilder();
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        var root = CreateToolVm("agent-1", "task", "InProgress", "{\"description\":\"Inspect repo\",\"agent_type\":\"general-purpose\",\"mode\":\"background\"}");
        builder.ProcessMessageToTranscript(root);

        var turn = Assert.Single(liveTurns);
        var subagent = Assert.IsType<SubagentToolCallItem>(Assert.Single(turn.Items));
        Assert.Equal("Inspect repo", subagent.Title);
        Assert.Equal("General purpose", subagent.DisplayName);
        Assert.True(subagent.IsExpanded);

        root.Message.ToolName = "agent:Coding Lumi";
        root.Message.Author = "Coding Lumi";
        root.Message.Content = "{\"description\":\"Inspect repo\",\"agentName\":\"Coding Lumi\",\"agentDisplayName\":\"Coding Lumi\",\"agentDescription\":\"Elite coding agent\",\"mode\":\"background\"}";
        root.NotifyContentChanged();

        Assert.Equal("Coding Lumi", subagent.DisplayName);
        Assert.Equal("Elite coding agent", subagent.AgentDescription);
        Assert.Equal("Background", subagent.ModeLabel);

        root.Message.ToolStatus = "Completed";
        root.NotifyToolStatusChanged();

        Assert.True(subagent.IsCompleted);
        Assert.False(subagent.IsExpanded);
    }

    [Fact]
    public void AuthoritativeTerminalRefresh_CollapsesReopenedSubagent()
    {
        var builder = CreateBuilder();
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        var root = CreateToolVm(
            "agent-1",
            "task",
            "InProgress",
            "{\"description\":\"Inspect repo\",\"agent_type\":\"explore\",\"mode\":\"background\"}");
        builder.ProcessMessageToTranscript(root);

        var turn = Assert.Single(liveTurns);
        var subagent = Assert.IsType<SubagentToolCallItem>(Assert.Single(turn.Items));

        // The successful wrapping task completion is deliberately deferred in production because it
        // only means the real sub-agent was spawned.
        Assert.False(ChatViewModel.ShouldApplyToolExecutionCompletionStatus("task", success: true));
        Assert.True(subagent.IsExpanded);

        // The authoritative subagent.completed event collapses the live card.
        builder.UpdateSubagentToolStatus("agent-1", "Completed");

        Assert.True(subagent.IsCompleted);
        Assert.False(subagent.IsExpanded);

        // Duplicate terminal events must also re-collapse a completed card the user reopened.
        subagent.IsExpanded = true;
        builder.UpdateSubagentToolStatus("agent-1", "Completed");
        Assert.False(subagent.IsExpanded);
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Failed")]
    [InlineData("Stopped")]
    public void LiveStandalone_TerminalStatusCollapses(string terminalStatus)
    {
        var builder = CreateBuilder();
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        builder.ProcessMessageToTranscript(CreateToolVm(
            "agent-1",
            "task",
            "InProgress",
            "{\"description\":\"Inspect repo\",\"agent_type\":\"explore\",\"mode\":\"background\"}"));

        var subagent = Assert.IsType<SubagentToolCallItem>(Assert.Single(Assert.Single(liveTurns).Items));
        Assert.True(subagent.IsActive);
        Assert.True(subagent.IsExpanded);

        builder.UpdateSubagentToolStatus("agent-1", terminalStatus);

        Assert.False(subagent.IsActive);
        Assert.False(subagent.IsExpanded);
    }

    [Fact]
    public void LiveGroup_PartialCompletionKeepsOnlyRunningWorkActive()
    {
        var (builder, group) = CreateLiveGroup();

        builder.UpdateSubagentToolStatus("agent-1", "Completed");

        Assert.True(group.IsActive);
        Assert.True(group.IsExpanded);
        Assert.Equal(1, group.DoneCount);
        Assert.Equal(1, group.RunningCount);
        Assert.False(group.Subagents[0].IsExpanded);
        Assert.True(group.Subagents[1].IsActive);
    }

    [Fact]
    public void LiveGroup_AllCompletedCollapses()
    {
        var (builder, group) = CreateLiveGroup();

        builder.UpdateSubagentToolStatus("agent-1", "Completed");
        builder.UpdateSubagentToolStatus("agent-2", "Completed");

        Assert.False(group.IsActive);
        Assert.False(group.IsExpanded);
        Assert.Equal(2, group.DoneCount);
        Assert.Equal(0, group.RunningCount);
    }

    [Fact]
    public void LiveGroup_FailedMemberCollapsesWithFailedBadge()
    {
        var (builder, group) = CreateLiveGroup();

        builder.UpdateSubagentToolStatus("agent-1", "Completed");
        builder.UpdateSubagentToolStatus("agent-2", "Failed");

        Assert.False(group.IsActive);
        Assert.False(group.IsExpanded);
        Assert.Equal(1, group.DoneCount);
        Assert.Equal(1, group.FailedCount);
        Assert.True(group.ShowFailedBadge);
    }

    [Fact]
    public void LiveGroup_StoppedMembersAreTerminalAndCollapse()
    {
        var (builder, group) = CreateLiveGroup();

        builder.UpdateSubagentToolStatus("agent-1", "Stopped");
        Assert.True(group.IsActive);
        Assert.True(group.IsExpanded);
        Assert.Equal(1, group.DoneCount);
        Assert.Equal(1, group.RunningCount);

        builder.UpdateSubagentToolStatus("agent-2", "Stopped");

        Assert.False(group.IsActive);
        Assert.False(group.IsExpanded);
        Assert.Equal(2, group.DoneCount);
        Assert.Equal(0, group.RunningCount);
    }

    [Fact]
    public void LiveGroup_DuplicateTerminalRefreshCollapsesReopenedMember()
    {
        var (builder, group) = CreateLiveGroup();
        var member = group.Subagents[0];

        member.IsExpanded = true;
        builder.UpdateSubagentToolStatus("agent-1", "Completed");
        Assert.False(member.IsExpanded);

        member.IsExpanded = true;

        builder.UpdateSubagentToolStatus("agent-1", "Completed");

        Assert.False(member.IsExpanded);
        Assert.True(group.IsActive); // sibling is still running
    }

    [Fact]
    public void Rebuild_StoppedGroupIsCollapsed()
    {
        var builder = CreateBuilder();
        var messages = new[]
        {
            CreateToolVm("agent-1", "task", "Stopped", "{\"description\":\"A\",\"agent_type\":\"research\"}"),
            CreateToolVm("agent-2", "task", "Stopped", "{\"description\":\"B\",\"agent_type\":\"research\"}"),
        };

        var group = Assert.IsType<SubagentGroupItem>(
            Assert.Single(Assert.Single(builder.Rebuild(messages)).Items));

        Assert.False(group.IsActive);
        Assert.False(group.IsExpanded);
        Assert.Equal(0, group.RunningCount);
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Failed")]
    [InlineData("Stopped")]
    public void Rebuild_TerminalStandaloneIsCollapsed(string terminalStatus)
    {
        var builder = CreateBuilder();
        var messages = new[]
        {
            CreateToolVm(
                "agent-1",
                "task",
                terminalStatus,
                "{\"description\":\"Inspect repo\",\"agent_type\":\"explore\"}")
        };

        var subagent = Assert.IsType<SubagentToolCallItem>(
            Assert.Single(Assert.Single(builder.Rebuild(messages)).Items));

        Assert.False(subagent.IsActive);
        Assert.False(subagent.IsExpanded);
    }

    [Fact]
    public void Rebuild_RunningStandaloneIsActiveButCollapsed()
    {
        var builder = CreateBuilder();
        var messages = new[]
        {
            CreateToolVm(
                "agent-1",
                "task",
                "InProgress",
                "{\"description\":\"Inspect repo\",\"agent_type\":\"explore\"}")
        };

        var subagent = Assert.IsType<SubagentToolCallItem>(
            Assert.Single(Assert.Single(builder.Rebuild(messages)).Items));

        Assert.True(subagent.IsActive);
        Assert.False(subagent.IsExpanded);
    }

    private static TranscriptBuilder CreateBuilder()
        => new(CreateDataStore(), _ => { }, (_, _) => { }, _ => { }, (_, _) => Task.CompletedTask, () => null);

    private static (TranscriptBuilder Builder, SubagentGroupItem Group) CreateLiveGroup()
    {
        var builder = CreateBuilder();
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        builder.ProcessMessageToTranscript(CreateToolVm(
            "agent-1",
            "task",
            "InProgress",
            "{\"description\":\"A\",\"agent_type\":\"research\",\"mode\":\"background\"}"));
        builder.ProcessMessageToTranscript(CreateToolVm(
            "agent-2",
            "task",
            "InProgress",
            "{\"description\":\"B\",\"agent_type\":\"research\",\"mode\":\"background\"}"));

        var group = Assert.IsType<SubagentGroupItem>(Assert.Single(Assert.Single(liveTurns).Items));
        Assert.True(group.IsActive);
        Assert.True(group.IsExpanded);
        Assert.All(group.Subagents, member => Assert.False(member.IsExpanded));
        return (builder, group);
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
