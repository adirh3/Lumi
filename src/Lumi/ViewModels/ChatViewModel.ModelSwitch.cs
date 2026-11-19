using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Copilot;
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
        SessionModelRoute route,
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
            await SessionConfigBuilder.EnsureProviderModelRegisteredAsync(
                session, route, options.ModelCapabilities, selectionToken);
            var sessionModelId = route.SessionModelId
                ?? throw new InvalidOperationException(Loc.Byok_Error_StaleModel);
            var result = await SessionModelSwitch.SwitchAsync(session, sessionModelId, options, selectionToken);
            if (result.Deferred)
                runtime.PendingModelSwitch = new PendingModelSwitch(sessionModelId, result.QueueIds);
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
    private async Task WaitForSessionRebuildAsync(Chat chat, string sessionModelId, CancellationToken selectionToken)
    {
        var runtime = GetOrCreateRuntimeState(chat.Id);
        await _modelSwitchGate.WaitAsync(selectionToken);
        try
        {
            if (runtime.PendingModelSwitch is { } waiting && _sessionCache.TryGetValue(chat.Id, out var session))
                await SessionModelSwitch.RemoveQueuedAsync(session, waiting.QueueIds);
            runtime.PendingModelSwitch = runtime.IsBusy
                ? new PendingModelSwitch(sessionModelId, [], NeedsNewSession: true)
                : null;
        }
        finally
        {
            _modelSwitchGate.Release();
            if (CurrentChat?.Id == chat.Id)
                RefreshModelSwitchNotice();
        }
    }

    private readonly record struct TurnModelSelection(string? Model, string? Effort, string? Tier);

    private TurnModelSelection ReadTurnModelSelection(Chat chat, bool useStoredModelOptions = false)
    {
        var model = ResolveSelectedModelForChat(chat);
        return new(
            model,
            useStoredModelOptions
                ? ResolveReasoningEffortForModel(chat.LastReasoningEffortUsed, model)
                : ResolvePersistedReasoningEffortForChat(chat, model),
            useStoredModelOptions
                ? ResolveContextWindowTierForModel(chat.LastContextWindowTierUsed, model)
                : ResolveSelectedContextWindowTierForChat(chat, model));
    }

    // False means Copilot queued the switch behind existing work. No prompt has reached it yet.
    private async Task<bool> PrepareSessionModelForTurnAsync(
        Chat chat, CopilotSession session, TurnModelSelection selection, CancellationToken cancellationToken,
        bool waitForDeferred = true)
    {
        var runtime = GetOrCreateRuntimeState(chat.Id);
        await _modelSwitchGate.WaitAsync(cancellationToken);
        try
        {
            var pending = runtime.PendingModelSwitch;
            var route = ResolveModelRouteForChat(selection.Model, chat);
            if (route.IsInvalidByok)
                throw new InvalidOperationException(Loc.Byok_Error_StaleModel);
            if (string.IsNullOrWhiteSpace(route.SessionModelId))
                return true;

            try
            {
                if (!IsCachedSessionProviderConsistentWithSelection(chat.Id, session))
                    throw new InvalidOperationException("The selected model requires a different session.");

                var capabilities = ByokConfigHelper.BuildModelCapabilitiesOverride(route.ByokModel, selection.Tier);
                var options = SessionConfigBuilder.CreateSetModelOptions(
                    route.SelectionToken, selection.Effort, selection.Tier, capabilities);
#pragma warning disable GHCP001
                async Task<bool> IsSelectedModelLiveAsync(bool defaultAutoRequested = false)
                {
                    var live = await session.Rpc.Model.GetCurrentAsync(cancellationToken);
                    var autoTier = live.PendingAutoTier ?? live.ActivatingAutoTier ?? live.AutoTier;
                    return (string.Equals(live.ModelId, route.SessionModelId, StringComparison.OrdinalIgnoreCase)
                            || (route.IsByok && ByokConfigHelper.MatchesWireModelId(_dataStore.Data.Settings, selection.Model, live.ModelId)))
                        && (options.ReasoningEffort is null
                            || string.Equals(options.ReasoningEffort, live.ReasoningEffort, StringComparison.OrdinalIgnoreCase))
                        && (options.ContextTier is null || options.ContextTier == (live.ContextTier ?? ContextTier.Default))
                        // Auto preferences can legitimately be staged for activation by this very send.
                        && (options.AutoTier is null || options.AutoTier == autoTier)
                        // The SDK projects a staged reset and an absent pending value both as null.
                        // If there is a committed tier, explicitly request the reset before submitting.
                        && (!options.ResetAutoTier || defaultAutoRequested || autoTier is null);
                }

                if (pending is { NeedsNewSession: false, QueueIds.Count: > 0 }
                    && waitForDeferred
                    && string.Equals(pending.SessionModelId, route.SessionModelId, StringComparison.OrdinalIgnoreCase))
                {
                    var queue = await session.Rpc.Queue.PendingItemsAsync(cancellationToken);
                    if (queue.Items.Any(item => pending.QueueIds.Contains(item.Id)))
                        return false;
                }
#pragma warning restore GHCP001

                if (pending is not null)
                {
                    await SessionModelSwitch.RemoveQueuedAsync(session, pending.QueueIds);
                    runtime.PendingModelSwitch = null;
                }

                if (await IsSelectedModelLiveAsync())
                    return true;

                await SessionConfigBuilder.EnsureProviderModelRegisteredAsync(
                    session, route, capabilities, cancellationToken);
                var result = await SessionModelSwitch.SwitchAsync(
                    session, route.SessionModelId, options, cancellationToken);
                if (result.Deferred)
                {
                    runtime.PendingModelSwitch = new PendingModelSwitch(route.SessionModelId, result.QueueIds);
                    if (waitForDeferred && result.QueueIds.Count > 0)
                        return false;
                }

                if (result.Deferred || !await IsSelectedModelLiveAsync(defaultAutoRequested: options.ResetAutoTier))
                    throw new InvalidOperationException("The runtime did not apply the requested model.");
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !IsCopilotTransportError(ex)
                && !IsSessionNotFoundError(ex))
            {
                Trace.TraceWarning("[Chat] Could not use the requested model {0}: {1}", route.SessionModelId, ex.Message);
                throw new InvalidOperationException(
                    Loc.Get("ModelSwitch_CannotUse", FormatModelDisplay(selection.Model) ?? route.SessionModelId), ex);
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
            || !string.Equals(pending.SessionModelId, newModel, StringComparison.OrdinalIgnoreCase))
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
            || runtime is not { PendingModelSwitch.NeedsNewSession: false, IsSwitchingModel: false, IsPreparingModelForTurn: false })
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
                canSwitchNow = !pending.NeedsNewSession && !runtime.IsPreparingModelForTurn;
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
