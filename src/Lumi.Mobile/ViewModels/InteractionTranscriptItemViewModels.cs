using CommunityToolkit.Mvvm.ComponentModel;
using Lumi.Remote.Protocol;
using StrataTheme.Controls;
using Strings = Lumi.Mobile.Localization.ChatSessionStrings;

namespace Lumi.Mobile.ViewModels;

public sealed partial class InputRequestItemViewModel : TranscriptItemViewModel
{
    private string? _definitionJson;
    private string? _pendingState;
    [ObservableProperty] private InteractiveDefinition? _definition;
    [ObservableProperty] private string? _stateJson;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    private string _inputStatus = "Expired";
    [ObservableProperty] private string _language = Strings.Language;
    public bool CanSubmit => InputStatus == "Waiting";
    public InputRequestItemViewModel(RemoteTranscriptItem item) : base(item) => Update(item);
    public override void Update(RemoteTranscriptItem item)
    {
        if (item.InputRequest is not { } request) return;
        if (_definitionJson != request.Definition)
        {
            Definition = InteractiveDefinition.Parse(request.Kind, request.Definition);
            _definitionJson = request.Definition;
        }
        if (request.StateJson == _pendingState)
            _pendingState = null;
        StateJson = request.Status == "Waiting" ? _pendingState ?? request.StateJson : request.StateJson;
        InputStatus = request.Status;
        Language = item.InteractionLanguage ?? Strings.Language;
    }
    internal void Stage(string json)
    {
        _pendingState = json;
        StateJson = json;
    }
    internal void Confirm(string json)
    {
        if (_pendingState == json)
            _pendingState = null;
    }
}

public sealed partial class InteractionReceiptItemViewModel : TranscriptItemViewModel
{
    [ObservableProperty] private RemoteInteractionReceipt _receipt = new();
    [ObservableProperty] private string _language = Strings.Language;
    public InteractionReceiptItemViewModel(RemoteTranscriptItem item) : base(item) => Update(item);
    public override void Update(RemoteTranscriptItem item)
    {
        Receipt = item.InteractionReceipt ?? new();
        Language = item.InteractionLanguage ?? Strings.Language;
    }
}
