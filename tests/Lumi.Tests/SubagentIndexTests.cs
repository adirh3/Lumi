using System;
using System.Linq;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// The Workspace's Agents page: how the index orders and filters a chat's runs, how the run page
/// pages through them, and the run details it presents (kind colour, duration, folded task).
/// </summary>
public sealed class SubagentIndexTests
{
    [Fact]
    public void Index_ListsLiveAgentsFirstThenTheNewestFinishedUnderSections()
    {
        Loc.Load("en");
        using var vm = CreateViewModel(
            Agent("a1", "Completed", "First"),
            Agent("a2", "InProgress", "Second"),
            Agent("a3", "Failed", "Third"),
            Agent("a4", "Completed", "Fourth"));

        Assert.Collection(
            vm.SubagentIndexRows,
            row => AssertSection(row, isLive: true, count: 1),
            row => Assert.Equal("Second", Run(row).Title),
            row => AssertSection(row, isLive: false, count: 3),
            row => Assert.Equal("Fourth", Run(row).Title),
            row => Assert.Equal("Third", Run(row).Title),
            row => Assert.Equal("First", Run(row).Title));
    }

    [Fact]
    public void Index_WithoutLiveWorkIsOnePlainListAndNoFilters()
    {
        Loc.Load("en");
        using var vm = CreateViewModel(
            Agent("a1", "Completed", "First"),
            Agent("a2", "Completed", "Second"));

        // Nothing to separate or narrow: no headings, no filter row.
        Assert.All(vm.SubagentIndexRows, static row => Assert.IsType<SubagentToolCallItem>(row));
        Assert.Equal(["Second", "First"], vm.SubagentIndexRows.Select(static row => Run(row).Title));
        Assert.False(vm.ShowSubagentFilters);
    }

    [Fact]
    public void Filters_OfferTheStatusesPresentAndFallBackWhenTheirRunsAreGone()
    {
        Loc.Load("en");
        using var vm = CreateViewModel(
            Agent("a1", "Completed", "First"),
            Agent("a2", "InProgress", "Second"),
            Agent("a3", "Failed", "Third"));

        Assert.True(vm.ShowSubagentFilters);
        Assert.Equal(
            [SubagentRunFilter.All, SubagentRunFilter.Running, SubagentRunFilter.Done, SubagentRunFilter.Failed],
            vm.SubagentFilterChips.Where(static chip => chip.IsAvailable).Select(static chip => chip.Filter));
        Assert.Equal("3", Chip(SubagentRunFilter.All).CountLabel);
        Assert.True(Chip(SubagentRunFilter.All).IsSelected);

        Chip(SubagentRunFilter.Failed).SelectCommand.Execute(null);

        Assert.Equal(SubagentRunFilter.Failed, vm.SubagentRunFilter);
        Assert.True(Chip(SubagentRunFilter.Failed).IsSelected);
        Assert.False(Chip(SubagentRunFilter.All).IsSelected);
        Assert.Equal("Third", Run(Assert.Single(vm.SubagentIndexRows)).Title);

        // Watching only the running agents: once the last one finishes, the index shows everything
        // again instead of an empty list.
        Chip(SubagentRunFilter.Running).SelectCommand.Execute(null);
        Assert.Equal("Second", Run(Assert.Single(vm.SubagentIndexRows)).Title);

        var running = vm.Messages.Single(message => message.Message.ToolCallId == "a2");
        running.Message.ToolStatus = "Completed";
        running.NotifyToolStatusChanged();

        Assert.Equal(SubagentRunFilter.All, vm.SubagentRunFilter);
        Assert.Equal(3, vm.SubagentIndexRows.Count);
        Assert.Equal(0, Chip(SubagentRunFilter.Running).Count);

        SubagentFilterChip Chip(SubagentRunFilter filter)
            => vm.SubagentFilterChips.Single(chip => chip.Filter == filter);
    }

    [Fact]
    public void Index_MovesAFinishedRunWithoutReplacingTheList()
    {
        Loc.Load("en");
        using var vm = CreateViewModel(
            Agent("a1", "InProgress", "First"),
            Agent("a2", "Completed", "Second"));
        var rows = vm.SubagentIndexRows;
        var first = vm.SubagentRuns.Single(static run => run.Title == "First");
        var second = vm.SubagentRuns.Single(static run => run.Title == "Second");
        Assert.Same(first, vm.SubagentIndexRows[1]); // under "Running now"

        var running = vm.Messages.Single(message => message.Message.ToolCallId == "a1");
        running.Message.ToolStatus = "Completed";
        running.NotifyToolStatusChanged();

        // The older run drops below the newer one, in the same list: a virtualized index keeps its
        // realized rows instead of re-creating them.
        Assert.Same(rows, vm.SubagentIndexRows);
        Assert.Equal(new object[] { second, first }, vm.SubagentIndexRows);
    }

    [Fact]
    public void Pager_KeepsPagingEveryRunWhenTheOpenRunIsOutsideTheFilter()
    {
        Loc.Load("en");
        using var vm = CreateViewModel(
            Agent("a1", "Failed", "First"),
            Agent("a2", "Completed", "Second"),
            Agent("a3", "Completed", "Third"));
        vm.SubagentFilterChips.Single(static chip => chip.Filter == SubagentRunFilter.Failed).SelectCommand.Execute(null);

        // A run the index's filter hides (opened from the chat) pages through every run...
        vm.OpenSubagentRunCommand.Execute(vm.SubagentRuns.Single(static run => run.Title == "Second"));
        Assert.Equal(Position(2, 3), vm.SubagentRunPositionText);

        // ...and keeps doing so after stepping onto a run the filter does show.
        vm.OpenNextSubagentRunCommand.Execute(null);
        Assert.Equal("First", vm.SelectedSubagentRun!.Title);
        Assert.Equal(Position(3, 3), vm.SubagentRunPositionText);
        Assert.True(vm.HasSubagentRunPager);
        Assert.True(vm.OpenPreviousSubagentRunCommand.CanExecute(null));

        // A run opened from the filtered index pages through that view.
        vm.BackToSubagentIndexCommand.Execute(null);
        vm.OpenSubagentRunCommand.Execute(vm.SubagentRuns.Single(static run => run.Title == "First"));
        Assert.Equal(Position(1, 1), vm.SubagentRunPositionText);
        Assert.False(vm.HasSubagentRunPager);
    }

    [Fact]
    public void Pager_StepsThroughTheRunsInIndexOrder()
    {
        Loc.Load("en");
        using var vm = CreateViewModel(
            Agent("a1", "Completed", "First"),
            Agent("a2", "Completed", "Second"),
            Agent("a3", "Completed", "Third"));

        vm.OpenSubagentRunCommand.Execute(vm.SubagentRuns.Single(static run => run.Title == "Second"));

        Assert.True(vm.HasSubagentRunPager);
        Assert.Equal(Position(2, 3), vm.SubagentRunPositionText);
        Assert.True(vm.OpenPreviousSubagentRunCommand.CanExecute(null));
        Assert.True(vm.OpenNextSubagentRunCommand.CanExecute(null));

        vm.OpenNextSubagentRunCommand.Execute(null);
        Assert.Equal("First", vm.SelectedSubagentRun!.Title);
        Assert.Equal(Position(3, 3), vm.SubagentRunPositionText);
        Assert.False(vm.OpenNextSubagentRunCommand.CanExecute(null));

        vm.OpenPreviousSubagentRunCommand.Execute(null);
        vm.OpenPreviousSubagentRunCommand.Execute(null);
        Assert.Equal("Third", vm.SelectedSubagentRun!.Title);
        Assert.Equal(Position(1, 3), vm.SubagentRunPositionText);
        Assert.False(vm.OpenPreviousSubagentRunCommand.CanExecute(null));

        vm.BackToSubagentIndexCommand.Execute(null);
        Assert.False(vm.HasSubagentRunPager);
    }

    [Fact]
    public void FailedRun_SaysWhyItFailed()
    {
        Loc.Load("en");
        var failed = Agent("a1", "Failed", "First");
        failed.ToolOutput = "402 You have exceeded your monthly quota";
        using var vm = CreateViewModel(failed, Agent("a2", "InProgress", "Second"));

        var first = vm.SubagentRuns.Single(static run => run.Title == "First");
        Assert.True(first.HasError);
        Assert.Equal("402 You have exceeded your monthly quota", first.ErrorText);

        // A run that fails while the chat is open reports its reason too; a running one has none.
        var second = vm.SubagentRuns.Single(static run => run.Title == "Second");
        Assert.False(second.HasError);
        var live = vm.Messages.Single(message => message.Message.ToolCallId == "a2");
        live.Message.ToolOutput = "No response generated (code: failure)";
        live.Message.ToolStatus = "Failed";
        live.NotifyToolStatusChanged();

        Assert.True(second.HasError);
        Assert.Equal("No response generated (code: failure)", second.ErrorText);
    }

    [Fact]
    public void AgentColour_IsStablePerKindOfAgent()
    {
        Assert.Equal(
            SubagentToolCallItem.AccentIndexFor("Code Review Agent"),
            SubagentToolCallItem.AccentIndexFor("code review agent"));
        Assert.All(
            new[] { "Task Agent", "Explore agent", "picker-review", "" },
            static name => Assert.InRange(SubagentToolCallItem.AccentIndexFor(name), 1, 6));

        var finished = new SubagentToolCallItem("Explore agent", StrataAiToolCallStatus.Completed);
        var running = new SubagentToolCallItem("Explore agent", StrataAiToolCallStatus.InProgress);
        Assert.Equal(finished.AccentIndex, running.AccentIndex);

        // subagent.started can name the agent after the run was created; it then wears its kind's colour.
        var renamed = new SubagentToolCallItem("Agent", StrataAiToolCallStatus.InProgress) { DisplayName = "Explore agent" };
        Assert.Equal(finished.AccentIndex, renamed.AccentIndex);
    }

    [Fact]
    public void Duration_ReadsLikeAClock()
    {
        Loc.Load("en");
        var run = new SubagentToolCallItem("Explore agent", StrataAiToolCallStatus.Completed) { DurationMs = 1_896_300 };
        Assert.Equal("31m 36s", run.DurationText);

        run.DurationMs = 4_100;
        Assert.Equal("4.1s", run.DurationText);

        run.DurationMs = 63;
        Assert.Equal("63 ms", run.DurationText);

        run.DurationMs = 0;
        Assert.Null(run.DurationText);
    }

    [Fact]
    public void LongTask_FoldsToAPlainPreviewUntilExpanded()
    {
        Loc.Load("en");
        var prompt = "## Scope\n\n- **Read** the `builder`\n\n" + string.Concat(Enumerable.Repeat("More detail. ", 40));
        var run = new SubagentToolCallItem("Explore agent", StrataAiToolCallStatus.Completed) { Prompt = prompt };

        Assert.True(run.IsPromptLong);
        // Folded, nothing is handed to the markdown renderer — only a bounded plain preview.
        Assert.Null(run.ExpandedPrompt);
        Assert.StartsWith("Scope\nRead the builder", run.PromptPreview);
        Assert.Equal(Loc.Subagent_ShowFullTask, run.PromptToggleLabel);

        run.TogglePromptCommand.Execute(null);
        Assert.Equal(prompt, run.ExpandedPrompt);
        Assert.Equal(Loc.Subagent_ShowLessTask, run.PromptToggleLabel);

        var brief = new SubagentToolCallItem("Explore agent", StrataAiToolCallStatus.Completed) { Prompt = "Look around." };
        Assert.False(brief.IsPromptLong);
        Assert.Equal("Look around.", brief.ExpandedPrompt);
    }

    [Fact]
    public void RunMeta_CountsStepsAndWhenTheRunStarted()
    {
        Loc.Load("en");
        var startedAt = DateTimeOffset.Now;
        var run = new SubagentToolCallItem("Explore agent", StrataAiToolCallStatus.InProgress) { StartedAt = startedAt };
        var started = string.Format(Loc.Culture, Loc.Subagent_StartedAt, startedAt.ToLocalTime().ToString("HH:mm", Loc.Culture));
        Assert.Equal(started, run.RunMetaText);

        run.Activities.Add(new ToolCallItem("📄 Reading file", StrataAiToolCallStatus.Completed, "tool:1"));
        Assert.Equal($"{Loc.Subagent_OneStep} · {started}", run.RunMetaText);

        run.Activities.Add(new ToolCallItem("📄 Reading file", StrataAiToolCallStatus.Completed, "tool:2"));
        Assert.StartsWith(string.Format(Loc.Culture, Loc.Subagent_Steps, 2), run.RunMetaText);
    }

    [Fact]
    public void RunPage_CountsTheRunsStepsAsItsCardDoes()
    {
        // A run of an older exchange is listed from its latest step only; its page reads the rest
        // and must arrive at the count the run's card has when the whole chat is built.
        Loc.Load("en");
        const string plan = """{"todoList":[{"title":"Look","status":"completed"},{"title":"Fix","status":"in-progress"}]}""";
        using var vm = CreateViewModel(Agent("run", "Completed", "Outer"));
        foreach (var message in new[]
        {
            Step("s1", "view", """{"path":"a.cs"}"""),
            Step("s2", "report_intent", """{"intent":"Reading"}"""),
            Step("s3", "update_todo", plan),
            Step("s4", "powershell", """{"command":"dir","description":"List files"}"""),
            Step("s5", "read_powershell", """{"shellId":"1"}"""),
            Step("s6", "update_todo", plan),
            Step("s7", "task", """{"description":"Inner","agent_type":"explore"}"""),
            Step("s8", "view", """{"path":"b.cs"}""", parent: "s7"),
            Step("s9", "grep", """{"pattern":"x"}"""),
        })
        {
            vm.Messages.Add(new ChatMessageViewModel(message));
        }

        var outer = vm.SubagentRuns.Single(run => run.ToolCallId == "run");
        // The file read, the plan (once, however often it changes), the command and the search.
        Assert.Equal(4, outer.StepCount);

        var page = new SubagentRunTranscript(new DataStore(new AppData()), static _ => { });
        page.Sync(outer, vm.Messages.ToList());
        Assert.Equal(outer.StepCount, page.StepCount);

        // A known count stands in for the steps the run's row has not loaded; zero hides it.
        var listed = new SubagentToolCallItem("Explore agent", StrataAiToolCallStatus.Completed) { KnownStepCount = 0 };
        listed.Activities.Add(new ToolCallItem("📄 Reading file", StrataAiToolCallStatus.Completed, "tool:latest"));
        Assert.False(listed.HasRunMeta);
        listed.KnownStepCount = page.StepCount;
        Assert.StartsWith(string.Format(Loc.Culture, Loc.Subagent_Steps, 4), listed.RunMetaText);
    }

    private static ChatMessage Step(string toolCallId, string toolName, string content, string parent = "run") => new()
    {
        Role = "tool",
        ToolName = toolName,
        ToolCallId = toolCallId,
        ParentToolCallId = parent,
        ToolStatus = "Completed",
        Content = content,
        Timestamp = DateTimeOffset.Now,
    };

    [Fact]
    public void ToolsWithoutStepCards_NeverAddAStepToTheirRun()
    {
        // The Agents index reads a run's latest step card from storage by tool name, so this list
        // must match what the builder files as a step.
        Loc.Load("en");
        const string plan = """{"todoList":[{"title":"Look","status":"completed"}]}""";
        foreach (var tool in TranscriptBuilder.ToolsWithoutStepCards)
        {
            var isPlan = tool is "update_todo" or "manage_todo_list";
            using var vm = CreateViewModel(Agent("run", "Completed", "Outer"));
            vm.Messages.Add(new ChatMessageViewModel(Step("s1", tool, isPlan ? plan : """{"intent":"x","filePath":"C:\\a.txt"}""")));
            vm.Messages.Add(new ChatMessageViewModel(Step("s2", tool, isPlan ? plan : """{"intent":"y","filePath":"C:\\b.txt"}""")));

            var run = vm.SubagentRuns.Single(run => run.ToolCallId == "run");
            // A plan is one card however often it changes; everything else adds none.
            Assert.True(run.StepCount == (isPlan ? 1 : 0), $"{tool} added {run.StepCount} steps.");
        }
    }

    private static ChatViewModel CreateViewModel(params ChatMessage[] messages)
    {
        var vm = new ChatViewModel(new DataStore(new AppData()), TestCopilot.Shared);
        foreach (var message in messages)
        {
            // Separate turns keep each run standalone rather than one parallel fan-out.
            vm.Messages.Add(new ChatMessageViewModel(new ChatMessage { Role = "user", Content = "Go" }));
            vm.Messages.Add(new ChatMessageViewModel(message));
        }

        return vm;
    }

    private static ChatMessage Agent(string toolCallId, string status, string description) => new()
    {
        Role = "tool",
        ToolName = "task",
        ToolCallId = toolCallId,
        ToolStatus = status,
        Content = $"{{\"description\":\"{description}\",\"agent_type\":\"explore\"}}",
        Timestamp = DateTimeOffset.Now,
    };

    private static SubagentToolCallItem Run(object row) => Assert.IsType<SubagentToolCallItem>(row);

    // Built from the loaded strings rather than literal English: Loc is process-wide, and another
    // test in the same run may have loaded a different language.
    private static string Position(int index, int count)
        => string.Format(Loc.Culture, Loc.Subagent_RunPosition, index, count);

    private static void AssertSection(object row, bool isLive, int count)
    {
        var section = Assert.IsType<SubagentIndexSection>(row);
        Assert.Equal(isLive, section.IsLive);
        Assert.Equal(count, section.Count);
    }
}
