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

    private static TranscriptBuilder CreateBuilder()
        => new(CreateDataStore(), _ => { }, (_, _) => { }, _ => { }, (_, _) => Task.CompletedTask, () => null);

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
