using Lumi.Services.Storage;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

/// <summary>What an older exchange's build reads of it (see <see cref="HistoryExchangeRows"/>).</summary>
public sealed class HistoryExchangeRowsTests
{
    private static StoredMessageHeader Row(long seq, string role, string? tool = null, string? id = null, string? parent = null, string? kind = null)
        => new(seq, role, kind, tool, id, parent);

    private static StoredMessageHeader Step(long seq, string tool, string id, string parent, string? kind = null)
        => Row(seq, "tool", tool, id, parent, kind);

    [Fact]
    public void Pick_LeavesOutTheStepsRunsTook_ButWhatShowsOutsideThem()
    {
        var outline = new ExchangeOutline(
            [
                Row(1, "user"),
                Row(2, "reasoning"),
                Row(3, "tool", "agent:explore", "run", kind: ChatMessageKinds.Agent),
                Step(4, "view", "s1", "run"),
                Step(5, "report_intent", "s2", "run", ChatMessageKinds.Intent),
                Step(6, "update_todo", "s3", "run"),
                Step(7, "edit", "s4", "run", ChatMessageKinds.Edit),
                Step(8, "workspace_file_changed", "s5", "run", ChatMessageKinds.ChangedFile),
                Step(9, "powershell", "s6", "run"),
                Step(10, "announce_file", "s7", "run", ChatMessageKinds.Deliverable),
                Row(11, "tool", "powershell", "own"),
                Row(12, "assistant"),
                Step(13, "manage_chats", "s8", "run", ChatMessageKinds.Link),
            ],
            [
                Step(20, "grep", "later1", "run"),
                Step(21, "view", "later2", "run"),
                Step(22, "read_agent", "later3", "run"),
            ],
            []);

        var picked = HistoryExchangeRows.Pick(outline);

        // The conversation, Lumi's own step, the run, what shows outside it, and the step its card
        // names as its latest (taken after the exchange); not its other plain steps.
        Assert.Equal([1L, 2, 3, 5, 6, 7, 8, 10, 11, 12, 13, 21], picked.Order());
    }

    [Fact]
    public void StepsThatShowOutsideTheirRun_CarryAFacet()
    {
        // A run's step is kept by its facet: a chat it opened (its chip shows in the turn) and a file
        // the session reported changed (the turn lists it) carry one.
        Assert.Equal(ChatMessageKinds.Link, ChatMessageKinds.Classify(new Lumi.Models.ChatMessage { Role = "tool", ToolName = "manage_chats", LinkedChatId = Guid.NewGuid() }));
        Assert.Equal(ChatMessageKinds.Link, ChatMessageKinds.Classify(new Lumi.Models.ChatMessage { Role = "tool", ToolName = "read_chat", LinkedChatId = Guid.NewGuid() }));
        Assert.Equal(ChatMessageKinds.ChangedFile, ChatMessageKinds.Classify(new Lumi.Models.ChatMessage { Role = "tool", ToolName = "workspace_file_changed" }));
        // An app's card shows in the turn, outside the run whose step opened it.
        Assert.Equal(ChatMessageKinds.App, ChatMessageKinds.Classify(new Lumi.Models.ChatMessage { Role = "tool", ToolName = "open_canvas", EmbeddedApp = new Lumi.Models.EmbeddedApp() }));
    }

    [Fact]
    public void Pick_KeepsTheLatestStepCard_WhenTheRunEndsInTheExchange()
    {
        var outline = new ExchangeOutline(
            [
                Row(1, "user"),
                Row(2, "tool", "task", "run", kind: ChatMessageKinds.Agent),
                Step(3, "view", "s1", "run"),
                Step(4, "powershell", "s2", "run"),
                Step(5, "report_intent", "s3", "run", ChatMessageKinds.Intent),
                Step(6, "task_complete", "s4", "run"),
                Row(7, "assistant"),
            ],
            [],
            []);

        Assert.Equal([1L, 2, 4, 5, 7], HistoryExchangeRows.Pick(outline).Order());
    }

    [Fact]
    public void Pick_KeepsTheStepsAKeptStepIsNestedUnder()
    {
        var outline = new ExchangeOutline(
            [
                Row(1, "user"),
                Row(2, "tool", "task", "run", kind: ChatMessageKinds.Agent),
                Step(3, "custom_tool", "wrapper", "run"),
                Step(4, "announce_file", "file", "wrapper", ChatMessageKinds.Deliverable),
                Step(5, "view", "s1", "run"),
                Row(6, "assistant"),
            ],
            [],
            []);

        // The deliverable is filed under the run through the wrapper, which it keeps.
        Assert.Equal([1L, 2, 3, 4, 5, 6], HistoryExchangeRows.Pick(outline).Order());
    }

    [Fact]
    public void Pick_LeavesOutTheStepsOfARunOfAnEarlierExchange()
    {
        var outline = new ExchangeOutline(
            [
                Row(10, "user"),
                Step(11, "view", "e1", "earlier"),
                Step(12, "web_search", "e2", "earlier", ChatMessageKinds.Web),
                Step(13, "view", "e3", "between"),
                Row(14, "assistant"),
            ],
            [],
            [new StoredToolCall("earlier", null, "agent:explore"), new StoredToolCall("between", "earlier", "custom_tool")]);

        // Its card is built with its own exchange; here its steps only feed a stand-in.
        Assert.Equal([10L, 12, 14], HistoryExchangeRows.Pick(outline).Order());
    }

    [Fact]
    public void Pick_KeepsAStepBuiltBeforeTheRunItNamesAsItsParent()
    {
        var outline = new ExchangeOutline(
            [
                Row(1, "user"),
                Step(2, "view", "early", "run"),
                Row(3, "tool", "task", "run", kind: ChatMessageKinds.Agent),
                Step(4, "view", "s1", "run"),
                Step(5, "view", "s2", "run"),
            ],
            [],
            []);

        // A build files a step under a run only once it has seen the run; before that it shows as Lumi's own.
        Assert.Equal([1L, 2, 3, 5], HistoryExchangeRows.Pick(outline).Order());
    }

    [Fact]
    public void OmittedBefore_MarksTheMessagesThatFollowLeftOutOnes()
    {
        var outline = new ExchangeOutline(
            [Row(1, "user"), Row(2, "reasoning"), Row(3, "tool"), Row(4, "tool"), Row(5, "reasoning"), Row(6, "tool"), Row(7, "assistant")],
            [],
            []);
        var read = new[] { 1L, 2, 5, 7 }.Select(static seq => new Lumi.Models.ChatMessage { StorageSeq = seq }).ToList();

        Assert.Equal([5L, 7], HistoryExchangeRows.OmittedBefore(outline, read).Order());
    }
}
