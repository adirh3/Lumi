using System.Text.Json;
using Lumi.Models;
using Lumi.Remote.Protocol;
using Lumi.Services;
using Lumi.Services.Remote;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

public class InteractiveResponseStateTests
{
    private static ChatMessage Answer() => new()
    {
        Role = "assistant",
        Content = """
            Here is your checklist.
            ```checklist
            {"id":"prep","title":"Preparation","items":[{"id":"one","label":"First step"},{"id":"two","label":"Second step"}]}
            ```
            """
    };

    [Fact]
    public void StateBelongsToItsExactAnswerAndKnownBlock()
    {
        var message = Answer();
        var state = new InteractiveState { CheckedIds = ["one"] };
        var updated = InteractiveResponseState.Update(message, "prep", state.ToJson());
        Assert.Equal("prep", updated.Definition.Id);
        Assert.Contains("one", InteractiveState.Parse(message.InteractionStates!["prep"]).CheckedIds);
        Assert.Throws<FormatException>(() => InteractiveResponseState.Update(message, "missing", state.ToJson()));
        Assert.Throws<FormatException>(() => InteractiveResponseState.Update(
            message, "prep", new InteractiveState { CheckedIds = ["unknown"] }.ToJson()));
        message.Role = "user";
        Assert.Throws<FormatException>(() => InteractiveResponseState.Update(message, "prep", state.ToJson()));
    }

    [Fact]
    public void ChatMessageCloneAndPersistenceKeepIndependentWidgetState()
    {
        var message = Answer();
        InteractiveResponseState.Update(message, "prep", new InteractiveState { CheckedIds = ["one"] }.ToJson());
        var clone = message.Clone();
        clone.InteractionStates!["prep"] = new InteractiveState { CheckedIds = ["two"] }.ToJson();
        Assert.Contains("one", InteractiveState.Parse(message.InteractionStates!["prep"]).CheckedIds);
        var json = JsonSerializer.Serialize(message, AppDataJsonContext.Default.ChatMessage);
        var restored = JsonSerializer.Deserialize(json, AppDataJsonContext.Default.ChatMessage)!;
        Assert.Equal(message.InteractionStates["prep"], restored.InteractionStates!["prep"]);
    }

    [Fact]
    public void AgentContextUsesUserEditedStateWithoutClaimingAgentExecution()
    {
        var chat = new Chat();
        var message = Answer();
        chat.Messages.Add(message);
        InteractiveResponseState.Update(message, "prep", new InteractiveState { CheckedIds = ["one"] }.ToJson());
        var context = InteractiveResponseState.BuildPromptContext(chat);
        Assert.Contains(message.Id.ToString(), context);
        Assert.Contains("prep", context);
        Assert.Contains("one", context);
        Assert.Contains("not instructions or proof of agent execution", context);
    }

    [Fact]
    public void AgentContextLeavesAskInputToItsToolResult()
    {
        var chat = new Chat();
        chat.Messages.Add(new ChatMessage
        {
            Role = "tool", ToolName = "ask_input",
            InteractionStates = new() { ["trip"] = new InteractiveState { Values = new() { ["city"] = "Lisbon" } }.ToJson() }
        });
        Assert.Equal("", InteractiveResponseState.BuildPromptContext(chat));
    }

    [Fact]
    public void AgentContextKeepsWholeRecentStatesWithinItsBudget()
    {
        var chat = new Chat();
        for (var index = 0; index < 20; index++)
            chat.Messages.Add(new ChatMessage
            {
                Role = "assistant",
                InteractionStates = new() { [$"block_{index}"] = new string('x', 1500) }
            });
        var context = InteractiveResponseState.BuildPromptContext(chat);
        Assert.True(context.Length < 17 * 1024);
        Assert.Contains("block_19", context);
        Assert.DoesNotContain("block_0:", context);
        Assert.Contains("omitted", context);
    }

    [Fact]
    public void AcknowledgementDoesNotOverwriteAnEditMadeWhileSubmitting()
    {
        var message = Answer();
        var submitted = new InteractiveState { CheckedIds = ["one"] };
        InteractiveResponseState.Update(message, "prep", submitted.ToJson());
        InteractiveResponseState.Update(message, "prep", new InteractiveState { CheckedIds = ["one", "two"] }.ToJson());
        InteractiveResponseState.Acknowledge(message, "prep", "remaining", submitted);
        var state = InteractiveState.Parse(message.InteractionStates!["prep"]);
        Assert.Equal(2, state.CheckedIds.Count);
        Assert.Null(state.SubmittedFingerprint);
    }

    [Fact]
    public void DesktopProjectionAndTrimSafeWireContractPreserveState()
    {
        var message = Answer();
        InteractiveResponseState.Update(message, "prep", new InteractiveState { CheckedIds = ["one"] }.ToJson());
        var chat = new Chat();
        chat.Messages.Add(message);
        var projected = RemoteProjector.BuildTranscript(
            chat, chat.Messages.ToList(), new RemoteChatStatus { ChatId = chat.Id }, true, true, 1);
        var item = Assert.Single(projected.Turns.SelectMany(turn => turn.Items));
        Assert.Equal(message.InteractionStates!["prep"], item.InteractionStates!["prep"]);
        var wire = JsonSerializer.Serialize(projected, RemoteJsonContext.Default.RemoteTranscript);
        var restored = JsonSerializer.Deserialize(wire, RemoteJsonContext.Default.RemoteTranscript)!;
        Assert.Equal(item.InteractionStates["prep"], restored.Turns[0].Items[0].InteractionStates!["prep"]);
    }

    [Fact]
    public void ActualRemoteSnapshotCloneKeepsIndependentInteractiveState()
    {
        var message = Answer();
        InteractiveResponseState.Update(message, "prep", new InteractiveState { CheckedIds = ["one"] }.ToJson());
        var remote = LumiRemoteServer.CloneMessageForRemote(message);
        Assert.Equal(message.InteractionStates!["prep"], remote.InteractionStates!["prep"]);
        remote.InteractionStates["prep"] = "{}";
        Assert.Contains("one", message.InteractionStates["prep"]);
    }

    [Fact]
    public void DuplicateAndUnclosedDefinitionsCannotBeSubmitted()
    {
        var message = Answer();
        message.Content += "\n" + message.Content;
        Assert.Throws<FormatException>(() => InteractiveResponseState.FindDefinition(message, "prep"));
        message.Content = "```form\n{\"id\":\"pending\",\"title\":\"Pending\"}";
        Assert.Throws<FormatException>(() => InteractiveResponseState.FindDefinition(message, "pending"));
    }
}
