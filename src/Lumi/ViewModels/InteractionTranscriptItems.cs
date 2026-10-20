using System;
using Lumi.Localization;
using Lumi.Models;
using StrataTheme.Controls;

namespace Lumi.ViewModels;

public sealed class InputRequestItem : TranscriptItem
{
    public ChatMessage Message { get; }
    public InteractiveDefinition Definition { get; }
    public string Language => Loc.Culture.Name;
    public string? StateJson => Message.InputAnswer ?? Message.InteractionStates?.GetValueOrDefault(Definition.Id);
    public string InputStatus => Message.InputCanceled ? "Canceled"
        : Message.InputAnswer is not null ? "Submitted"
        : Message.ToolStatus == "InProgress" ? "Waiting" : "Expired";
    public bool CanSubmit => InputStatus == "Waiting";

    public InputRequestItem(ChatMessage message) : base($"input:{message.Id}")
    {
        Message = message;
        Definition = InteractiveDefinition.Parse(message.InputKind!, message.InputDefinition!);
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(StateJson));
        OnPropertyChanged(nameof(InputStatus));
        OnPropertyChanged(nameof(CanSubmit));
    }
}

public sealed class InteractionReceiptItem : TranscriptItem
{
    public InteractionReceipt Receipt { get; }
    public string Language => Loc.Culture.Name;
    public Guid MessageId { get; }

    public InteractionReceiptItem(ChatMessage message) : base($"message:action:{message.Id}")
    {
        MessageId = message.Id;
        Receipt = message.InteractionReceipt!;
    }
}
