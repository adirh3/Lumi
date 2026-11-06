using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHub.Copilot;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;

namespace Lumi.ViewModels;

/// <summary>What the bar above the composer says about a model picked while Lumi works.</summary>
public enum ModelSwitchNotice
{
    None,
    /// <summary>
    /// The pick waits for the running reply to end. "Switch now" can move the work over at once, unless the
    /// pick needs a new session (another endpoint), which only the next message starts.
    /// </summary>
    Pending,
    /// <summary>"Switch now" is moving the running work to the pick.</summary>
    Switching,
    /// <summary>For a moment after the running work took the pick: a new reasoning effort, or "Switch now".</summary>
    Applied
}

/// <summary>
/// Changing the model or reasoning effort while Lumi works. The runtime applies a new reasoning effort from
/// the running turn's next step, but switches models only between turns, so a model picked mid-reply waits
/// in its queue. The bar above the composer says when the pick takes over and offers "Switch now", which
/// interrupts the running step the way Pause does and resumes the same work on the new model.
/// </summary>
public partial class ChatViewModel
{
    private static readonly TimeSpan ModelSwitchAppliedDuration = TimeSpan.FromSeconds(4);

    /// <summary>Serializes switching a session's model, so a newer pick always replaces the one queued.</summary>
    private readonly SemaphoreSlim _modelSwitchGate = new(1, 1);
    private (Guid ChatId, string Selection)? _modelSwitchApplied;
    private CancellationTokenSource? _modelSwitchAppliedCts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModelSwitchNotice), nameof(IsModelSwitchPending),
        nameof(IsModelSwitchInProgress), nameof(IsModelSwitchApplied))]
    private ModelSwitchNotice _modelSwitchNotice;

    [ObservableProperty] private string _modelSwitchNoticeText = "";
    [ObservableProperty] private string _switchModelNowToolTip = "";

    /// <summary>A pick waits for the reply and can take over the running work now.</summary>
    [ObservableProperty] private bool _canSwitchModelNow;

    public bool HasModelSwitchNotice => ModelSwitchNotice != ModelSwitchNotice.None;
    public bool IsModelSwitchPending => ModelSwitchNotice == ModelSwitchNotice.Pending;
    public bool IsModelSwitchInProgress => ModelSwitchNotice == ModelSwitchNotice.Switching;
    public bool IsModelSwitchApplied => ModelSwitchNotice == ModelSwitchNotice.Applied;

    /// <summary>"Switch now" passes through a pause, which the chat on screen shouldn't show as one.</summary>
    private bool IsCurrentChatSwitchingModel
        => CurrentChat is { } chat && _runtimeStates.TryGetValue(chat.Id, out var runtime) && runtime.IsSwitchingModel;

    /// <summary>
    /// Switches the chat's live session to a pick and remembers whether the runtime queued it until the
    /// running turn ends. A newer pick replaces the one waiting, so the queue never holds two.
    /// </summary>
    private async Task SwitchSessionModelAsync(
        Chat chat,
        CopilotSession session,
        string wireModelId,
        SetModelOptions options,
        CancellationToken selectionToken)
    {
        var runtime = GetOrCreateRuntimeState(chat.Id);
        await _modelSwitchGate.WaitAsync(selectionToken);
        try
        {
            if (runtime.PendingModelSwitch is { } waiting)
                await SessionModelSwitch.RemoveQueuedAsync(session, waiting.QueueIds);
            runtime.PendingModelSwitch = null;
            var result = await SessionModelSwitch.SwitchAsync(session, wireModelId, options);
            if (result.Deferred)
                runtime.PendingModelSwitch = new PendingModelSwitch(wireModelId, result.QueueIds);
            else if (runtime.IsBusy && !chat.IsPaused)
                ShowModelSwitchApplied(chat);
        }
        finally
        {
            _modelSwitchGate.Release();
            if (CurrentChat?.Id == chat.Id)
                RefreshModelSwitchNotice();
        }
    }

    /// <summary>
    /// A pick for another endpoint queues nothing: the running turn keeps its endpoint, and the next
    /// message starts a session on the pick's (a session can't move to another endpoint, so the running
    /// work can't continue there).
    /// </summary>
    private async Task WaitForSessionRebuildAsync(Chat chat, string wireModelId, CancellationToken selectionToken)
    {
        var runtime = GetOrCreateRuntimeState(chat.Id);
        await _modelSwitchGate.WaitAsync(selectionToken);
        try
        {
            if (runtime.PendingModelSwitch is { } waiting && _sessionCache.TryGetValue(chat.Id, out var session))
                await SessionModelSwitch.RemoveQueuedAsync(session, waiting.QueueIds);
            runtime.PendingModelSwitch = runtime.IsBusy
                ? new PendingModelSwitch(wireModelId, [], NeedsNewSession: true)
                : null;
        }
        finally
        {
            _modelSwitchGate.Release();
            if (CurrentChat?.Id == chat.Id)
                RefreshModelSwitchNotice();
        }
    }

    /// <summary>
    /// Before a turn starts on the session, applies the chat's pick if a switch still waits for it. An abort
    /// (Stop, Pause, Send now, Switch now) drops a queued switch with its turn; one still queued gives way, so
    /// the queue never holds two.
    /// </summary>
    private async Task ApplyPendingModelSwitchAsync(Chat chat, CopilotSession session)
    {
        if (!_runtimeStates.TryGetValue(chat.Id, out var runtime) || runtime.PendingModelSwitch is null)
            return;

        await _modelSwitchGate.WaitAsync();
        try
        {
            if (runtime.PendingModelSwitch is not { } pending)
                return;

            await SessionModelSwitch.RemoveQueuedAsync(session, pending.QueueIds);
            runtime.PendingModelSwitch = null;
            // A session rebuilt for the pick's endpoint already runs it.
            if (pending.NeedsNewSession || !IsCachedSessionProviderConsistentWithSelection(chat.Id, session))
                return;

            var selection = ResolveSelectedModelForChat(chat);
            var route = ResolveModelRouteForChat(selection, chat);
            if (route.IsInvalidByok || string.IsNullOrWhiteSpace(route.WireModelId))
                return;

            var effort = ResolvePersistedReasoningEffortForChat(chat, selection);
            var tier = ResolveSelectedContextWindowTierForChat(chat, selection);
            try
            {
                var result = await SessionModelSwitch.SwitchAsync(
                    session,
                    route.WireModelId,
                    SessionConfigBuilder.CreateSetModelOptions(
                        route.SelectionToken, effort, tier,
                        ByokConfigHelper.BuildModelCapabilitiesOverride(route.ByokModel, tier)));
                if (result.Deferred)
                    runtime.PendingModelSwitch = new PendingModelSwitch(route.WireModelId, result.QueueIds);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("[Chat] Could not apply the model picked during the last reply: {0}", ex);
                // The session can't take the pick, so the next message rebuilds it; until then the pick waits.
                _pendingSessionInvalidations.Add(chat.Id);
                runtime.PendingModelSwitch = new PendingModelSwitch(route.WireModelId, [], NeedsNewSession: true);
            }
        }
        finally
        {
            _modelSwitchGate.Release();
            if (CurrentChat?.Id == chat.Id)
                RefreshModelSwitchNotice();
        }
    }

    /// <summary>The runtime applied the queued switch when the reply ended: the pick no longer waits.</summary>
    private void ConfirmModelSwitch(Chat chat, ChatRuntimeState runtime, string? newModel)
    {
        if (runtime.PendingModelSwitch is not { NeedsNewSession: false } pending
            || !string.Equals(pending.WireModelId, newModel, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        runtime.PendingModelSwitch = null;
        if (CurrentChat?.Id == chat.Id)
            RefreshModelSwitchNotice();
    }

    /// <summary>
    /// Moves the running work to the picked model now. The runtime switches models only between turns, so
    /// this interrupts the running step as Pause does (dropping the queued switch) and resumes the same work:
    /// the resume applies the pick first, and the agent picks the interrupted step up on the new model.
    /// </summary>
    [RelayCommand]
    private async Task SwitchModelNow()
    {
        if (CurrentChat is not { } chat
            || chat.IsPaused
            || !_runtimeStates.TryGetValue(chat.Id, out var runtime)
            || runtime is not { PendingModelSwitch.NeedsNewSession: false, IsSwitchingModel: false })
        {
            return;
        }

        SetModelSwitching(chat, runtime, switching: true);
        string? error;
        try
        {
            error = await TrySetChatPausedAsync(chat, paused: true);
            // A Stop pressed during the switch ends the work instead.
            if (error is null && chat.IsPaused)
                error = await TrySetChatPausedAsync(chat, paused: false);
        }
        finally
        {
            SetModelSwitching(chat, runtime, switching: false);
        }

        if (chat.IsPaused)
        {
            // The work couldn't resume, so the chat stays paused and holds its queued messages as a pause does.
            SetChatPaused(chat, paused: true);
            return;
        }

        if (error is null && runtime is { PendingModelSwitch: null, IsBusy: true })
            ShowModelSwitchApplied(chat);
    }

    private void SetModelSwitching(Chat chat, ChatRuntimeState runtime, bool switching)
    {
        runtime.IsSwitchingModel = switching;
        if (CurrentChat?.Id == chat.Id)
            NotifyChatPausePropertiesChanged();
    }

    /// <summary>For a moment, says the running work now uses the chat's pick.</summary>
    private void ShowModelSwitchApplied(Chat chat)
    {
        _modelSwitchAppliedCts?.Cancel();
        var cancellation = _modelSwitchAppliedCts = new CancellationTokenSource();
        _modelSwitchApplied = (chat.Id, DescribeModelSelection(chat));
        _ = Task.Delay(ModelSwitchAppliedDuration, cancellation.Token).ContinueWith(
            _ => Dispatcher.UIThread.Post(() =>
            {
                if (_isDisposed || !ReferenceEquals(_modelSwitchAppliedCts, cancellation))
                    return;

                _modelSwitchApplied = null;
                RefreshModelSwitchNotice();
            }),
            cancellation.Token,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
        if (CurrentChat?.Id == chat.Id)
            RefreshModelSwitchNotice();
    }

    /// <summary>The chat's pick as the picker names it: the model, and its reasoning effort when it has one.</summary>
    private string DescribeModelSelection(Chat chat)
    {
        var model = ResolveSelectedModelForChat(chat);
        var name = FormatModelDisplay(model) ?? "";
        var effort = ResolvePersistedReasoningEffortForChat(chat, model);
        return string.IsNullOrWhiteSpace(effort)
            ? name
            : $"{name} · {ModelSelectionHelper.EffortToDisplay(effort)}";
    }

    /// <summary>Recomputes the bar above the composer for the chat on screen.</summary>
    private void RefreshModelSwitchNotice()
    {
        var notice = ModelSwitchNotice.None;
        var text = "";
        var toolTip = "";
        var canSwitchNow = false;
        if (CurrentChat is { } chat)
        {
            var runtime = _runtimeStates.GetValueOrDefault(chat.Id);
            if (runtime is { IsSwitchingModel: true })
            {
                notice = ModelSwitchNotice.Switching;
                text = Loc.Get(nameof(Loc.ModelSwitch_Switching), DescribeModelSelection(chat));
            }
            else if (runtime is { PendingModelSwitch: { } pending, IsBusy: true } && !chat.IsPaused)
            {
                var selection = DescribeModelSelection(chat);
                notice = ModelSwitchNotice.Pending;
                canSwitchNow = !pending.NeedsNewSession;
                text = canSwitchNow
                    ? Loc.Get(nameof(Loc.ModelSwitch_Pending), selection)
                    : Loc.Get(nameof(Loc.ModelSwitch_NextMessage), selection);
                if (canSwitchNow)
                    toolTip = Loc.Get(nameof(Loc.ModelSwitch_SwitchNowToolTip), selection);
            }
            else if (_modelSwitchApplied is { } applied && applied.ChatId == chat.Id)
            {
                notice = ModelSwitchNotice.Applied;
                text = Loc.Get(nameof(Loc.ModelSwitch_Applied), applied.Selection);
            }
        }

        ModelSwitchNoticeText = text;
        SwitchModelNowToolTip = toolTip;
        CanSwitchModelNow = canSwitchNow;
        ModelSwitchNotice = notice;
    }
}
