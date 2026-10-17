using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Converters;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// The work trail at a glance: every step says what kind of work it is with a line icon rather than an emoji,
/// shows the one fact worth seeing (the file, the query, the command, the reasoning's opening line), groups
/// say what they are doing and whether something failed, and a plan at work lists its steps.
/// </summary>
[Collection("Headless UI")]
public sealed class TranscriptWorkTrailGlanceTests
{
    [Theory]
    [InlineData("powershell", TranscriptIcons.Terminal)]
    [InlineData("read_powershell", TranscriptIcons.Terminal)]
    [InlineData("view", TranscriptIcons.Read)]
    [InlineData("edit", TranscriptIcons.Edit)]
    [InlineData("apply_patch", TranscriptIcons.Edit)]
    [InlineData("create", TranscriptIcons.Create)]
    [InlineData("grep", TranscriptIcons.Search)]
    [InlineData("web_search", TranscriptIcons.Search)]
    [InlineData("web_fetch", TranscriptIcons.Page)]
    [InlineData(ToolDisplayHelper.BrowserOpenToolName, TranscriptIcons.Web)]
    [InlineData(ToolDisplayHelper.BrowserScreenshotToolName, TranscriptIcons.Screenshot)]
    [InlineData("ui_click", TranscriptIcons.Desktop)]
    [InlineData("manage_todo_list", TranscriptIcons.Plan)]
    [InlineData("skill", TranscriptIcons.Skill)]
    [InlineData("task", TranscriptIcons.Agent)]
    [InlineData("some_connector_tool", TranscriptIcons.Tool)]
    public void ToolIconKey_NamesTheKindOfWork(string toolName, string expected)
        => Assert.Equal(expected, ToolDisplayHelper.GetToolIconKey(toolName));

    [Fact]
    public async Task EveryIconKey_DrawsItsOwnLineIcon()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var keys = typeof(TranscriptIcons).GetFields()
                .Where(field => field.IsLiteral)
                .Select(field => (string)field.GetValue(null)!)
                .ToList();

            Assert.NotEmpty(keys);
            // Only the generic tool goes without an icon of its own.
            foreach (var key in keys.Where(key => key != TranscriptIcons.Tool))
                Assert.True(TranscriptIconConverter.TryResolve(key) is not null, $"'{key}' has no icon of its own.");
            Assert.NotNull(TranscriptIconConverter.Resolve(TranscriptIcons.Tool));
        }, CancellationToken.None);
    }

    [Fact]
    public void ToolStep_CarriesItsIconAndGlanceDetailWithoutAnEmojiInItsName()
    {
        var (builder, turns) = CreateLiveBuilder();
        builder.ProcessMessageToTranscript(CreateToolVm("read-1", "view", "Completed", "{\"path\":\"src/Program.cs\"}"));
        builder.ProcessMessageToTranscript(CreateToolVm("search-1", "web_search", "Completed", "{\"query\":\"oled lag\"}"));

        var group = Assert.IsType<ToolGroupItem>(Assert.Single(Assert.Single(turns).Items));
        var read = Assert.IsType<ToolCallItem>(group.ToolCalls[0]);
        var search = Assert.IsType<ToolCallItem>(group.ToolCalls[1]);

        Assert.Equal(TranscriptIcons.Read, read.IconKey);
        Assert.Equal("Program.cs", read.Detail);
        Assert.Equal(TranscriptIcons.Search, search.IconKey);
        Assert.Equal("oled lag", search.Detail);
        Assert.DoesNotContain(ToolDisplayHelper.GetToolGlyph("view"), read.ToolName, StringComparison.Ordinal);
        Assert.DoesNotContain(ToolDisplayHelper.GetToolGlyph("web_search"), search.ToolName, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolGroup_ShowsTheRunningStepThenTheSharedKindOfWork()
    {
        var (builder, turns) = CreateLiveBuilder();
        var first = CreateToolVm("search-1", "web_search", "InProgress", "{\"query\":\"one\"}");
        var second = CreateToolVm("search-2", "web_search", "InProgress", "{\"query\":\"two\"}");
        var startedAt = DateTimeOffset.Now.AddSeconds(-12);
        first.Message.ToolStartedAt = startedAt;
        builder.ProcessMessageToTranscript(first);
        builder.ProcessMessageToTranscript(second);

        var group = Assert.IsType<ToolGroupItem>(Assert.Single(Assert.Single(turns).Items));
        Assert.True(group.IsActive);
        Assert.Equal(TranscriptIcons.Search, group.IconKey);
        Assert.Equal(TranscriptIcons.Search, group.ActivityPreview[0].IconKey);
        Assert.False(group.ActivityPreview[0].HasSteps);
        // Each live row counts up from when its step started.
        Assert.Equal(startedAt, group.ActivityPreview[0].RunningSince);

        foreach (var message in new[] { first, second })
        {
            message.Message.ToolStatus = "Completed";
            message.NotifyToolStatusChanged();
        }

        Assert.False(group.IsActive);
        Assert.Equal(TranscriptIcons.Search, group.IconKey);
        Assert.False(group.HasFailures);
    }

    [Fact]
    public void ToolGroup_WithMixedWork_ShowsLayersAndMarksAFailure()
    {
        var (builder, turns) = CreateLiveBuilder();
        builder.ProcessMessageToTranscript(CreateToolVm("read-1", "view", "Completed", "{\"path\":\"a.txt\"}"));
        builder.ProcessMessageToTranscript(CreateToolVm("edit-1", "edit", "Failed", "{\"path\":\"a.txt\",\"old_str\":\"a\",\"new_str\":\"b\"}"));

        var group = Assert.IsType<ToolGroupItem>(Assert.Single(Assert.Single(turns).Items));
        Assert.Equal(TranscriptIcons.Mixed, group.IconKey);
        Assert.True(group.HasFailures);
    }

    [Fact]
    public void Command_ReadsAsPlainWordsAndItsChipShowsTheCommandAndTime()
    {
        Lumi.Localization.Loc.Load("en");
        var (builder, turns) = CreateLiveBuilder();
        var message = CreateToolVm("shell-1", "powershell", "Completed", "{\"command\":\"dotnet test\\nexit\"}");
        message.Message.ToolDurationMs = 4200;
        builder.ProcessMessageToTranscript(message);
        builder.CloseCurrentToolGroup();

        var chip = Assert.IsType<SingleToolItem>(Assert.Single(Assert.Single(turns).Items));
        var terminal = Assert.IsType<TerminalPreviewItem>(chip.Inner);
        Assert.Equal(TranscriptIcons.Terminal, chip.IconKey);
        Assert.Equal(Lumi.Localization.Loc.Get("Chat_CommandCompleted"), terminal.ToolName);
        Assert.Equal("dotnet test … · 4.2s", chip.Meta);
        Assert.False(chip.HasError);
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Failed")]
    [InlineData("Stopped")]
    public void Command_IsNamedByThePurposeTheAgentGaveIt_WhileRunningAndAfter(string finalStatus)
    {
        Lumi.Localization.Loc.Load("en");
        var (builder, turns) = CreateLiveBuilder();
        var message = CreateToolVm("shell-1", "powershell", "InProgress",
            "{\"command\":\"dotnet test tests/Lumi.Tests\",\"description\":\"run the focused tests\"}");
        builder.ProcessMessageToTranscript(message);

        var group = Assert.IsType<ToolGroupItem>(Assert.Single(Assert.Single(turns).Items));
        var running = Assert.Single(group.ActivityPreview);
        Assert.Equal("Run the focused tests", running.Label);
        Assert.Equal("dotnet test tests/Lumi.Tests", running.Detail);
        Assert.StartsWith("Run the focused tests", builder.LiveActivityLabel, StringComparison.Ordinal);

        message.Message.ToolStatus = finalStatus;
        message.NotifyToolStatusChanged();
        builder.CloseCurrentToolGroup();

        var chip = Assert.IsType<SingleToolItem>(Assert.Single(Assert.Single(turns).Items));
        Assert.Equal("Run the focused tests", chip.Label);
        Assert.Equal(finalStatus == "Failed", chip.HasError);
        // Stopped has no colour of its own, so the glance line says it.
        Assert.Equal(finalStatus == "Stopped", chip.Meta?.StartsWith("Stopped · dotnet test", StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData("powershell", "{\"command\":\"dotnet build\",\"description\":\"build Lumi\"}", null, null, "Build Lumi")]
    [InlineData("powershell", "{\"command\":\"dotnet build\"}", null, null, "Running command")]
    [InlineData("github-mcp-server-search_issues", "{}", "Search issues and pull requests", "search_issues", "Search issues and pull requests")]
    [InlineData("github-mcp-server-search_issues", "{}", null, "search_issues", "Search issues")]
    [InlineData("example_lookup", "{}", null, null, "Example lookup")]
    // Lumi's own wording wins for tools it knows.
    [InlineData("view", "{\"path\":\"a.txt\"}", "View file", null, "Reading a.txt")]
    public void StepName_UsesTheAgentsPurposeOrTheServersTitleWhenLumiHasNoWording(
        string toolName, string args, string? descriptorTitle, string? mcpToolName, string expected)
    {
        Lumi.Localization.Loc.Load("en");
        var title = ToolDisplayHelper.GetToolTitle(descriptorTitle, mcpToolName);

        Assert.Equal(expected, ToolDisplayHelper.FormatToolStatusName(toolName, args, toolTitle: title));
    }

    [Fact]
    public void RunningTool_ShowsItsProgressMessage_ThenItsDetailOnceDone()
    {
        var (builder, turns) = CreateLiveBuilder();
        var lookup = CreateToolVm("lookup-1", "example_lookup", "InProgress", "{\"query\":\"OLED\"}");
        var other = CreateToolVm("read-1", "view", "InProgress", "{\"path\":\"a.txt\"}");
        builder.ProcessMessageToTranscript(lookup);
        builder.ProcessMessageToTranscript(other);

        var group = Assert.IsType<ToolGroupItem>(Assert.Single(Assert.Single(turns).Items));
        var row = Assert.IsType<ToolCallItem>(group.ToolCalls[0]);
        Assert.Equal("OLED", row.LiveDetail);

        builder.UpdateToolProgress("lookup-1", "Indexing 120 of 400 files\nmore detail");
        Assert.Equal("Indexing 120 of 400 files", row.LiveDetail);
        Assert.Equal("Indexing 120 of 400 files", group.ActivityPreview[0].Detail);

        lookup.Message.ToolStatus = "Completed";
        lookup.NotifyToolStatusChanged();
        Assert.Equal("OLED", row.LiveDetail);

        // A late progress message for a finished step changes nothing.
        builder.UpdateToolProgress("lookup-1", "Indexing 400 of 400 files");
        Assert.Equal("OLED", row.LiveDetail);
    }

    [Fact]
    public void LivePlan_ListsItsStepsInTheCardAndNamesTheStepAtWork()
    {
        var (builder, turns) = CreateLiveBuilder();
        builder.ProcessMessageToTranscript(CreateToolVm("plan-1", "manage_todo_list", "InProgress",
            PlanJson(("Gather the data", "completed"), ("Group the calls", "in-progress"), ("Summarize", "not-started"))));

        var group = Assert.IsType<ToolGroupItem>(Assert.Single(Assert.Single(turns).Items));
        Assert.True(group.IsActive);
        Assert.Equal(TranscriptIcons.Plan, group.IconKey);
        var plan = Assert.Single(group.ActivityPreview);
        Assert.True(plan.HasSteps);
        Assert.Equal("Group the calls", plan.Label);
        Assert.Equal(new[] { "Gather the data", "Group the calls", "Summarize" }, plan.Steps!.Select(step => step.Title));
        Assert.True(plan.Steps![0].IsDone);
        Assert.True(plan.Steps![1].IsCurrent);
        Assert.False(plan.Steps![2].IsDone || plan.Steps![2].IsCurrent);

        builder.ProcessMessageToTranscript(CreateToolVm("plan-2", "manage_todo_list", "InProgress",
            PlanJson(("Gather the data", "completed"), ("Group the calls", "completed"), ("Summarize", "in-progress"))));

        plan = Assert.Single(group.ActivityPreview);
        Assert.Equal("Summarize", plan.Label);
        Assert.True(plan.Steps![1].IsDone);
        Assert.True(plan.Steps![2].IsCurrent);
    }

    [Fact]
    public void PlanChecklist_HasOneStepAtWork()
    {
        // The CLI's update_todo sends a markdown checklist, where every unchecked line reads as in progress.
        var steps = TranscriptBuilder.BuildPlanSteps(ToolDisplayHelper.ParseTodoSteps(
            "{\"todos\":\"- [x] Gather the data\\n- [ ] Group the calls\\n- [ ] Summarize\"}"));

        Assert.Equal(new[] { true, false, false }, steps.Select(step => step.IsDone));
        Assert.Equal(new[] { false, true, false }, steps.Select(step => step.IsCurrent));
    }

    [Theory]
    [InlineData(3, 1, 0, 3)]
    [InlineData(4, 3, 0, 4)]
    [InlineData(8, 0, 0, 4)]
    [InlineData(8, 3, 2, 4)]
    [InlineData(8, 6, 4, 4)]
    [InlineData(8, -1, 4, 4)]
    public void PlanGlance_ShowsAShortPlanWholeAndFourStepsAroundTheOneAtWork(
        int count, int current, int expectedStart, int expectedCount)
    {
        // Steps before the current one are done; with no current step, every step is done.
        var steps = Enumerable.Range(0, count)
            .Select(index => new PlanStepItem(
                $"Step {index}",
                IsDone: current < 0 || index < current,
                IsCurrent: index == current,
                IsFailed: false))
            .ToList();
        var plan = new TodoProgressItem("Plan", StrataAiToolCallStatus.InProgress) { Steps = steps };

        Assert.Equal(
            Enumerable.Range(expectedStart, expectedCount).Select(index => $"Step {index}"),
            plan.GlanceSteps.Select(step => step.Title));
    }

    [Theory]
    [InlineData("rtings.com|lg.com|RTINGS.com|example.com|a.com", "rtings.com, lg.com +2")]
    [InlineData("rtings.com|lg.com", "rtings.com, lg.com")]
    [InlineData("rtings.com", "rtings.com")]
    [InlineData("| ", null)]
    public void SourcesDomains_NameTheFirstTwoSitesAndCountTheRest(string domains, string? expected)
        => Assert.Equal(expected, AssistantMessageItem.SummarizeDomains(domains.Split('|')));

    [Theory]
    // The row already shows the detail, so the opened step does not repeat it.
    [InlineData("best OLED TV", "best OLED TV", null)]
    // A failure arrives as the detail, a blank line and the error: only the error is new.
    [InlineData("Busy\r\n\r\nThe server returned a failure.", "Busy", "The server returned a failure.")]
    [InlineData("Found 12 results", "best OLED TV", "Found 12 results")]
    [InlineData("Busybody text", "Busy", "Busybody text")]
    [InlineData("Found 12 results", null, "Found 12 results")]
    [InlineData("  ", "best OLED TV", null)]
    public void StepResult_LeavesOutTheGlanceDetailTheRowAlreadyShows(string info, string? detail, string? expected)
        => Assert.Equal(expected, ToolCallItem.WithoutGlanceDetail(info, detail));

    [Theory]
    [InlineData(@"C:\Users\adirh\AppData\Local\Temp\debug-fixtures", "\u2026\\Temp\\debug-fixtures")]
    [InlineData("/home/adir/src/lumi/Views", "\u2026/lumi/Views")]
    [InlineData(@"src\Views", @"src\Views")]
    [InlineData(null, null)]
    public void ChangedFile_ShowsTheLastTwoFoldersOfItsDirectory(string? directory, string? expected)
        => Assert.Equal(expected, FileChangeItem.CompactDirectory(directory));

    [Theory]
    [InlineData("Reasoned · 2 actions", "reasoned · 2 actions")]
    [InlineData("Reading a.txt", "reading a.txt")]
    // An acronym or a name keeps its capitals.
    [InlineData("PR review", "PR review")]
    [InlineData("GitHub triage", "GitHub triage")]
    [InlineData("", "")]
    public void EarlierWorkLabel_LowercasesOnlyAnOrdinaryFirstWord(string label, string expected)
        => Assert.Equal(expected, TranscriptBuilder.LowercaseFirst(label));

    [Theory]
    [InlineData("**Inspecting the logs**\n\nThe build failed on line 4.", false, "Inspecting the logs")]
    [InlineData("\n  - First, read the transcript.\nThen fix it.", false, "First, read the transcript.")]
    [InlineData("Checking `TranscriptBuilder`.\nNow comparing the two lists", true, "Now comparing the two lists")]
    [InlineData("Line one\n\n   \n", true, "Line one")]
    [InlineData("   \n\n", false, null)]
    public void ReasoningSummary_IsItsOpeningLineOrWhatItIsWritingNow(string content, bool fromEnd, string? expected)
        => Assert.Equal(expected, ReasoningItem.SummarizeReasoning(content, fromEnd));

    [Fact]
    public void Reasoning_GlanceLineFollowsTheStreamAndHidesWhileOpen()
    {
        var source = new ChatMessageViewModel(new ChatMessage
        {
            Role = "reasoning",
            Content = "**Planning**\nReading the file",
            IsStreaming = true,
            Timestamp = DateTimeOffset.Now,
        });
        var reasoning = new ReasoningItem(source, expandWhileStreaming: false);

        Assert.Equal("Reading the file", reasoning.Summary);

        source.Message.IsStreaming = false;
        source.IsStreaming = false;
        Assert.False(reasoning.IsActive);
        Assert.Equal("Planning", reasoning.Summary);

        reasoning.IsExpanded = true;
        Assert.Null(reasoning.Summary);
        reasoning.IsExpanded = false;
        Assert.Equal("Planning", reasoning.Summary);
    }

    private static string PlanJson(params (string Title, string Status)[] steps)
        => "{\"todoList\":[" + string.Join(",", steps.Select(step => $"{{\"title\":\"{step.Title}\",\"status\":\"{step.Status}\"}}")) + "]}";

    private static (TranscriptBuilder Builder, ObservableCollection<TranscriptTurn> Turns) CreateLiveBuilder()
    {
        var builder = new TranscriptBuilder(CreateDataStore(), _ => { }, (_, _) => { }, _ => { },
            (_, _) => Task.CompletedTask, () => null);
        var turns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(turns);
        return (builder, turns);
    }

    private static ChatMessageViewModel CreateToolVm(string toolCallId, string toolName, string toolStatus, string content)
        => new(new ChatMessage
        {
            Role = "tool",
            ToolCallId = toolCallId,
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
        var data = new AppData();
        data.Settings.ShowToolCalls = true;
        typeof(DataStore).GetField("_data", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(store, data);
        return store;
    }
}
