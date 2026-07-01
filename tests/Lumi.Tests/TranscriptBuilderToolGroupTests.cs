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
        Assert.NotNull(group.StreamingSummary);

        builder.CloseCurrentToolGroup();
        builder.CollapseCompletedBlocksInCurrentTurn();

        group = Assert.IsType<ToolGroupItem>(Assert.Single(Assert.Single(liveTurns).Items));
        Assert.True(group.IsActive);
        Assert.False(group.IsExpanded);
        Assert.Null(group.StreamingSummary);
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

    private static TranscriptBuilder CreateBuilder(bool showToolCalls = true)
        => new(CreateDataStore(showToolCalls), _ => { }, (_, _) => { }, _ => { }, (_, _) => Task.CompletedTask, () => null);

    private static void AssertCompactFinishedToolGroup(TranscriptItem item, int expectedToolCalls)
    {
        var group = Assert.IsType<ToolGroupItem>(item);
        Assert.False(group.IsActive);
        Assert.False(group.IsExpanded);
        Assert.Null(group.StreamingSummary);
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

    private static ChatMessageViewModel CreateReasoningVm(string content)
        => new(new ChatMessage
        {
            Role = "reasoning",
            Content = content,
            Author = "Thinking",
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
