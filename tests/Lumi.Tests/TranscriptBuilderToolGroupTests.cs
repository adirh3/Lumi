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

public sealed class TranscriptBuilderToolGroupTests
{
    [Fact]
    public void ProcessMessageToTranscript_StreamingToolGroup_StaysCollapsedAndShowsSummary()
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
        Assert.NotNull(group.StreamingSummary);
        Assert.Contains("notes.txt", group.StreamingSummary, StringComparison.Ordinal);
        Assert.Contains("Running command", group.StreamingSummary, StringComparison.Ordinal);

        firstTool.Message.ToolStatus = "Completed";
        firstTool.NotifyToolStatusChanged();
        Assert.True(group.IsActive);
        Assert.NotNull(group.StreamingSummary);

        secondTool.Message.ToolStatus = "Completed";
        secondTool.NotifyToolStatusChanged();

        Assert.False(group.IsActive);
        Assert.Null(group.StreamingSummary);
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

    private static TranscriptBuilder CreateBuilder()
        => new(CreateDataStore(), _ => { }, (_, _) => { }, (_, _) => Task.CompletedTask, () => null);

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

    private static ChatMessageViewModel CreateAssistantVm(string content)
        => new(new ChatMessage
        {
            Role = "assistant",
            Content = content,
            Author = "Lumi",
            Timestamp = DateTimeOffset.Now,
        });

    private static ChatMessageViewModel CreateReasoningVm(string content)
        => new(new ChatMessage
        {
            Role = "reasoning",
            Content = content,
            Author = "Thinking",
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
