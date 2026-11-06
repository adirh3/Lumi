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
    public string InputStatus => StatusOf(Message);
    public bool CanSubmit => InputStatus == "Waiting";

    public InputRequestItem(ChatMessage message) : base(StableIdFor(message.Id))
    {
        Message = message;
        Definition = InteractiveDefinition.Parse(message.InputKind!, message.InputDefinition!);
    }

    internal static string StableIdFor(Guid messageId) => $"input:{messageId}";

    /// <summary>Where an <c>ask_input</c> request stands: Waiting, Submitted, Canceled or Expired.</summary>
    internal static string StatusOf(ChatMessage message) => message.InputCanceled ? "Canceled"
        : message.InputAnswer is not null ? "Submitted"
        : message.ToolStatus == "InProgress" ? "Waiting" : "Expired";

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
    public DateTimeOffset SentAt { get; }

    public InteractionReceiptItem(ChatMessage message) : base(StableIdFor(message.Id))
    {
        MessageId = message.Id;
        SentAt = message.Timestamp;
        Receipt = message.InteractionReceipt!;
    }

    internal static string StableIdFor(Guid messageId) => $"message:action:{messageId}";
}
