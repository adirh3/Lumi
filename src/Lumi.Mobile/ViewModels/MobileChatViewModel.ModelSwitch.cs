using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Remote.Protocol;
using Strings = Lumi.Mobile.Localization.ChatSessionStrings;

namespace Lumi.Mobile.ViewModels;

public partial class MobileChatViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowModelSwitch), nameof(IsModelSwitchApplied), nameof(CanSwitchModelNow))]
    [NotifyCanExecuteChangedFor(nameof(SwitchModelNowCommand))]
    private RemoteModelSwitchState _modelSwitch;

    [ObservableProperty] private string? _modelSwitchText;
    [ObservableProperty] private string? _modelSwitchError;

    public bool ShowModelSwitch => ModelSwitch != RemoteModelSwitchState.None;
    public bool IsModelSwitchApplied => ModelSwitch == RemoteModelSwitchState.Applied;
    public bool CanSwitchModelNow => ModelSwitch == RemoteModelSwitchState.AfterReply;

    private bool IsSupersededModelSwitch(RemoteChatStatus status) =>
        (_pendingConfiguration.TryGetScalar("model", out var model)
            && !string.Equals(model, status.Model, StringComparison.OrdinalIgnoreCase))
        || (_pendingConfiguration.TryGetScalar("quality", out var quality)
            && (status.Quality is null || EffortDisplay(quality) != EffortDisplay(status.Quality)));

    private void ApplyModelSwitchStatus(RemoteChatStatus status)
    {
        var key = status.ModelSwitch switch
        {
            RemoteModelSwitchState.AfterReply => "ModelSwitchAfterReply",
            RemoteModelSwitchState.NextMessage => "ModelSwitchNextMessage",
            RemoteModelSwitchState.Switching => "ModelSwitchSwitching",
            RemoteModelSwitchState.Applied => "ModelSwitchApplied",
            _ => null
        };
        if (key is null || string.IsNullOrWhiteSpace(status.Model) || ChatId == Guid.Empty
            || (status.ModelSwitch != RemoteModelSwitchState.Switching && IsSupersededModelSwitch(status)))
        {
            ModelSwitch = RemoteModelSwitchState.None;
            ModelSwitchText = null;
            return;
        }

        var name = DisplayModel(status.Model) ?? status.Model;
        var selection = status.Quality is { Length: > 0 } quality ? $"{name} · {EffortDisplay(quality)}" : name;
        ModelSwitchText = Strings.Get(key, selection);
        ModelSwitch = status.ModelSwitch;
    }

    private void ReportModelConfigurationFailure(PendingChatConfiguration attempted, string? error)
    {
        if (IsStillPending("model") || IsStillPending("quality"))
            ModelSwitchError = error ?? Strings.Get("ModelSwitchFailed");

        bool IsStillPending(string key) =>
            attempted.TryGetScalar(key, out var value)
            && _pendingConfiguration.TryGetScalar(key, out var current)
            && value == current;
    }

    [RelayCommand(CanExecute = nameof(CanSwitchModelNow))]
    private async Task SwitchModelNowAsync()
    {
        if (!CanSwitchModelNow || ChatId == Guid.Empty || Model is not { Length: > 0 } model)
            return;

        var surface = CurrentSurface;
        var activation = _surfaceActivationGeneration;
        var host = Volatile.Read(ref _hostGeneration);
        ModelSwitchError = null;
        string? error;
        try
        {
            var result = await _sink.SendCommandAsync(new RemoteCommand(RemoteProtocol.Actions.SwitchModelNow)
                .With("chatId", surface.ChatId.ToString())
                .With("model", model));
            error = result.Ok ? null : result.Error ?? Strings.Get("ModelSwitchFailed");
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("[Mobile] Could not switch models: {0}", ex);
            error = Strings.Get("ModelSwitchFailed");
        }

        if (error is not null && IsCurrentHost(host) && IsCurrentSurfaceActivation(surface, activation))
            ModelSwitchError = error;
    }
}
