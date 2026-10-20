using Lumi.Mobile.ViewModels;
using Lumi.Remote.Protocol;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Mobile.Tests;

public class InteractiveResponseTests
{
    private static (MobileChatViewModel Chat, AssistantItemViewModel Item) Create(Sink sink)
    {
        var id = Guid.NewGuid();
        var chat = new MobileChatViewModel(sink);
        chat.Reset(id, "Interactive");
        chat.ApplyTranscript(new RemoteTranscript
        {
            ChatId = id, Title = "Interactive", Revision = 1,
            Turns = [new() { Id = "turn", Items = [new() { Id = Guid.NewGuid().ToString("N"), Text = "Answer" }] }]
        });
        return (chat, Assert.IsType<AssistantItemViewModel>(chat.Turns.Single().Items.Single()));
    }

    [Fact]
    public async Task InteractionTargetsExplicitIdsAndDoesNotConsumeTheDraft()
    {
        var sink = new Sink();
        var (chat, item) = Create(sink);
        chat.PromptText = "Keep this unrelated draft";
        chat.Attachments.Add(new PendingAttachment("notes.txt", @"C:\uploads\notes.txt"));
        var state = new InteractiveState { CheckedIds = ["one"] }.ToJson();
        var result = await chat.RequestInteractionAsync(item.Id, "prep", state, null);
        Assert.True(result.Accepted);
        var command = Assert.Single(sink.Commands);
        Assert.Equal(RemoteProtocol.Actions.UpdateInteraction, command.Action);
        Assert.Equal(chat.ChatId.ToString(), command.Get("chatId"));
        Assert.Equal(item.Id, command.Get("messageId"));
        Assert.Equal("prep", command.Get("blockId"));
        Assert.Null(command.Get("action"));
        Assert.Equal("Keep this unrelated draft", chat.PromptText);
        Assert.Single(chat.Attachments);
    }

    [Fact]
    public async Task UnknownOutcomeRetriesTheSameRequestWithoutInventingSuccess()
    {
        var sink = new Sink { Result = new RemoteCommandResult { IsOutcomeUnknown = true, Error = "No acknowledgement" } };
        var (chat, item) = Create(sink);
        var state = new InteractiveState { SelectedIds = ["a"] }.ToJson();
        var first = await chat.RequestInteractionAsync(item.Id, "options", state, "use");
        Assert.False(first.Accepted);
        sink.Result = new RemoteCommandResult { Ok = true };
        Assert.True((await chat.RequestInteractionAsync(item.Id, "options", state, "use")).Accepted);
        Assert.Equal(sink.Commands[0].RequestId, sink.Commands[1].RequestId);
    }

    [Fact]
    public async Task BusyAndOlderHostsDoNotDispatchAnAction()
    {
        var sink = new Sink { SupportsInteractiveResponses = false };
        var (chat, item) = Create(sink);
        Assert.False((await chat.RequestInteractionAsync(item.Id, "prep", "{}", null)).Accepted);
        Assert.Empty(sink.Commands);
        sink.SupportsInteractiveResponses = true;
        chat.IsBusy = true;
        Assert.False(chat.CanSubmitInteractions);
        Assert.False((await chat.RequestInteractionAsync(item.Id, "prep", "{}", "remaining")).Accepted);
        Assert.Empty(sink.Commands);
    }

    [Fact]
    public void LateServerEchoDoesNotEraseNewerLocalInput()
    {
        var item = new AssistantItemViewModel(new RemoteTranscriptItem { Id = "answer", Text = "Answer" });
        var first = new InteractiveState { Values = new() { ["budget"] = "100" } }.ToJson();
        var latest = new InteractiveState { Values = new() { ["budget"] = "200" } }.ToJson();
        item.StageInteractionState("form", first);
        item.StageInteractionState("form", latest);
        item.ConfirmInteractionState("form", first);
        item.Update(new RemoteTranscriptItem { Id = "answer", Text = "Answer", InteractionStates = new() { ["form"] = first } });
        Assert.Equal(latest, item.InteractionStates!["form"]);
        item.ConfirmInteractionState("form", latest);
        item.Update(new RemoteTranscriptItem { Id = "answer", Text = "Answer", InteractionStates = new() { ["form"] = latest } });
        Assert.Equal(latest, item.InteractionStates["form"]);
    }

    [Fact]
    public void InteractivePickerParticipatesInTheExistingModalPolicy()
    {
        var (chat, _) = Create(new Sink());
        Assert.False(chat.HasOpenSheet);
        chat.IsInteractivePickerOpen = true;
        Assert.True(chat.HasOpenSheet);
        chat.IsInteractivePickerOpen = false;
        Assert.False(chat.HasOpenSheet);
    }

    [Fact]
    public async Task RapidEditsAreSerializedAndQueuedIntermediateValuesAreCoalesced()
    {
        var sink = new Sink();
        var firstWrite = new TaskCompletionSource<RemoteCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        sink.PendingFirstWrite = firstWrite.Task;
        var (chat, item) = Create(sink);
        string State(string value) => new InteractiveState { Values = new() { ["budget"] = value } }.ToJson();
        var first = chat.RequestInteractionAsync(item.Id, "form", State("100"), null);
        var second = chat.RequestInteractionAsync(item.Id, "form", State("200"), null);
        var third = chat.RequestInteractionAsync(item.Id, "form", State("300"), null);
        Assert.Single(sink.Commands);
        firstWrite.SetResult(new RemoteCommandResult { Ok = true });
        Assert.All(await Task.WhenAll(first, second, third), result => Assert.True(result.Accepted));
        Assert.Equal(2, sink.Commands.Count);
        Assert.Equal("300", InteractiveState.Parse(sink.Commands[1].Get("state")!).Values["budget"]);
    }

    private sealed class Sink : IRemoteCommandSink
    {
        public bool SupportsInteractiveResponses { get; set; } = true;
        public List<RemoteCommand> Commands { get; } = [];
        public RemoteCommandResult Result { get; set; } = new() { Ok = true };
        public Task<RemoteCommandResult>? PendingFirstWrite { get; set; }
        public Task<RemoteCommandResult> SendCommandAsync(RemoteCommand command)
        {
            Commands.Add(command);
            if (Commands.Count == 1 && PendingFirstWrite is { } pending)
                return pending;
            return Task.FromResult(Result);
        }
        public Task<RemoteUploadResponse> UploadAsync(string fileName, ReadOnlyMemory<byte> content) =>
            Task.FromResult(new RemoteUploadResponse());
    }

    [Fact]
    public async Task WaitingInputCanSubmitWhileTheAssistantIsBusyWithoutSendingANewMessage()
    {
        var sink = new Sink();
        var id = Guid.NewGuid();
        var chat = new MobileChatViewModel(sink);
        chat.Reset(id, "Waiting");
        chat.ApplyTranscript(new RemoteTranscript
        {
            ChatId = id, Revision = 1,
            Status = new() { ChatId = id, IsBusy = true, IsAwaitingInput = true },
            Turns = [new() { Id = "turn", Items = [new()
            {
                Id = "input-message", Kind = RemoteProtocol.ItemKinds.Input,
                InputRequest = new() { Kind = "form", Status = "Waiting",
                    Definition = """{"id":"preferences","title":"Preferences","fields":[{"id":"name","label":"Name"}]}""" }
            }] }]
        });
        chat.PromptText = "Keep this draft";
        Assert.True(chat.IsAwaitingInput);
        Assert.False(chat.ShowThinking);
        Assert.True((await chat.RequestInteractionAsync("input-message", "preferences",
            new InteractiveState { Values = new() { ["name"] = "Adir" } }.ToJson(), "submit")).Accepted);
        Assert.Equal(RemoteProtocol.Actions.UpdateInteraction, Assert.Single(sink.Commands).Action);
        Assert.Equal("Keep this draft", chat.PromptText);
        Assert.Single(chat.Turns.Single().Items);
        Assert.Equal("Submitted", Assert.IsType<InputRequestItemViewModel>(chat.Turns.Single().Items.Single()).InputStatus);
    }

    [Fact]
    public void BackClosesAnOpenPickerAndAnotherChatNeverInheritsIt()
    {
        var (chat, _) = Create(new Sink());
        chat.IsInteractivePickerOpen = true;
        Assert.True(chat.HasOpenSheet);
        Assert.True(chat.DismissTopmostSheet());
        Assert.False(chat.IsInteractivePickerOpen);
        chat.IsInteractivePickerOpen = true;
        chat.Reset(Guid.NewGuid(), "Next");
        Assert.False(chat.HasOpenSheet);
    }

    [Fact]
    public void InputFramesKeepTheDefinitionAndNewerDraftAlive()
    {
        var remote = new RemoteTranscriptItem
        {
            Id = "input", Kind = RemoteProtocol.ItemKinds.Input,
            InputRequest = new() { Status = "Waiting", Kind = "form",
                Definition = """{"id":"form","title":"Form","fields":[{"id":"name","label":"Name"}]}""",
                StateJson = new InteractiveState { Values = new() { ["name"] = "Old" } }.ToJson() }
        };
        var item = new InputRequestItemViewModel(remote);
        var definition = item.Definition;
        var edited = new InteractiveState { Values = new() { ["name"] = "New" } }.ToJson();
        item.Stage(edited);
        item.Update(remote);
        Assert.Same(definition, item.Definition);
        Assert.Equal(edited, item.StateJson);
    }

    [Fact]
    public void ReceiptProjectionUsesADistinctCompactItem()
    {
        var remote = new RemoteTranscriptItem
        {
            Id = "action", Kind = RemoteProtocol.ItemKinds.User, Text = "Full model prompt",
            InteractionReceipt = new() { Title = "Compare 2 results", Summary = "Garden \u00b7 City", Detail = "Frozen inputs" }
        };
        var item = Assert.IsType<InteractionReceiptItemViewModel>(TranscriptItemFactory.Create(remote));
        Assert.Equal("Frozen inputs", item.Receipt.Detail);
        Assert.True(TranscriptItemFactory.CanReuse(item, remote));
        remote.InteractionReceipt = null;
        Assert.False(TranscriptItemFactory.CanReuse(item, remote));
    }
}
