using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using GitHub.Copilot;

namespace Lumi.ViewModels;

public partial class ChatViewModel
{
    internal static SessionHooks BuildSessionHooks() => new()
    {
        OnPreToolUse = async (_, _) => new PreToolUseHookOutput { PermissionDecision = "allow" },
        OnErrorOccurred = async (input, _) =>
        {
            if (input.Recoverable || CopilotService.IsTransientServerAuthError(input.Error))
                return new ErrorOccurredHookOutput { ErrorHandling = "retry", RetryCount = 3 };
            return new ErrorOccurredHookOutput { ErrorHandling = "abort" };
        }
    };

    private async Task SendPauseAwareAsync(
        Chat chat, CopilotSession session, MessageOptions options, CancellationToken cancellationToken)
    {
        await GetOrCreateRuntimeState(chat.Id).PauseGate.WaitAsync(cancellationToken);
        await session.SendAsync(options, cancellationToken);
    }

    public bool IsPaused => CurrentChat?.IsPaused == true;
    public bool IsPausePending => CurrentChat?.IsPausePending == true;
    public bool CanPauseChat => CurrentChat is not null && (IsBusy || IsSessionActive) && !IsPaused;
    public bool IsComposerBusy => IsBusy && !IsPaused;
    public bool IsTranscriptStreaming => IsStreaming && !IsPaused;
    public string PauseStatusTitle => Loc.Get(IsPausePending ? "Chat_Pausing" : "Chat_Paused");
    public string PauseStatusDescription => PauseError ?? Loc.Get(
        IsPausePending ? "Chat_PausingDescription" : "Chat_PauseDescription");

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    [CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(PauseStatusDescription))]
    private string? _pauseError;

    [RelayCommand]
    private async Task TogglePause()
    {
        if (CurrentChat is { } chat)
            await TrySetChatPausedAsync(chat, !chat.IsPaused);
    }

    [RelayCommand]
    private async Task ResumeChat()
    {
        if (CurrentChat is { } chat)
            await TrySetChatPausedAsync(chat, paused: false);
    }

    internal Task<string?> TrySetChatPausedAsync(Chat chat, bool paused)
    {
        var runtime = GetOrCreateRuntimeState(chat.Id);
        if (runtime.PauseResumeOperation is { IsCompleted: false } pending)
            return SetPauseAfterTransitionAsync(chat, paused, pending);

        var operation = runtime.PauseResumeOperation = SetChatPausedCoreAsync(chat, runtime, paused);
        _ = operation.ContinueWith(
            _ => Dispatcher.UIThread.Post(() =>
            {
                if (!_isDisposed)
                {
                    OnPropertyChanged(nameof(IsSessionActive));
                    RefreshChatPauseState(runtime);
                }
            }),
            TaskScheduler.Default);
        return operation;
    }

    private async Task<string?> SetPauseAfterTransitionAsync(Chat chat, bool paused, Task<string?> pending)
    {
        var error = await pending;
        return error ?? await TrySetChatPausedAsync(chat, paused);
    }

    private async Task<string?> SetChatPausedCoreAsync(Chat chat, ChatRuntimeState runtime, bool paused)
    {
        if (chat.IsPaused == paused)
            return null;

        PauseError = null;
        try
        {
            if (paused)
            {
                var hasSubmittedTurn = HasSubmittedCopilotTurn(runtime) && !runtime.PauseGate.IsWaiting;
                chat.PauseNeedsContinuation = hasSubmittedTurn;
                SetChatPaused(chat, true);
                if (!hasSubmittedTurn)
                    return null;

                if (_sessionCache.TryGetValue(chat.Id, out var session))
                {
#pragma warning disable GHCP001
                    await session.Rpc.Queue.SetDrainPausedAsync(true);
#pragma warning restore GHCP001
                }
                var stopError = await StopGenerationWithIntentAsync(
                    chat, resolvePendingSteersAsFailed: true, preservePause: true);
                if (stopError is not null)
                    throw new InvalidOperationException(stopError);
                RefreshChatPauseState(runtime);
                return null;
            }

            if (!chat.PauseNeedsContinuation)
            {
                SetChatPaused(chat, false);
                return null;
            }

            if (string.IsNullOrWhiteSpace(chat.CopilotSessionId))
                throw new InvalidOperationException(Loc.Status_OriginalSessionUnavailable);
            if (!_copilotService.IsConnected)
                await _copilotService.ConnectAsync();
            if (NeedsSessionSetup(chat)
                && !await EnsureSessionAsync(chat, CancellationToken.None, allowCreateFallback: false))
                throw new InvalidOperationException(Loc.Status_OriginalSessionUnavailable);
            if (!_sessionCache.TryGetValue(chat.Id, out var resumedSession))
                throw new InvalidOperationException(Loc.Status_OriginalSessionUnavailable);

            var cancellation = new CancellationTokenSource();
            _ctsSources[chat.Id] = cancellation;
            ClearManualStopRequested(chat.Id);
            BeginChatLifecycleTurn(chat);
            runtime.IsContinuationTurn = true;
            MarkRuntimeActive(runtime, Loc.Status_Thinking);
            chat.PauseNeedsContinuation = false;
            SetChatPaused(chat, false);
            if (CurrentChat?.Id == chat.Id)
                ApplyDisplayedRuntimeState(runtime);
            await AcquireByokRateSlotAsync(chat, cancellation.Token);
#pragma warning disable GHCP001
            await resumedSession.Rpc.Queue.SetDrainPausedAsync(false, cancellation.Token);
            await resumedSession.Rpc.SendMessagesAsync([], cancellationToken: cancellation.Token);
#pragma warning restore GHCP001
            QueueSaveChatIndex(chat);
            return null;
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceWarning($"[Chat] {(paused ? "Pause" : "Resume")} failed for {chat.Id}: {error}");
            if (paused)
            {
                chat.PauseNeedsContinuation = false;
                SetChatPaused(chat, false);
                if (_sessionCache.TryGetValue(chat.Id, out var session))
                {
#pragma warning disable GHCP001
                    try { await session.Rpc.Queue.SetDrainPausedAsync(false); }
                    catch (Exception releaseError)
                    {
                        System.Diagnostics.Trace.TraceWarning($"[Chat] Could not release queue pause for {chat.Id}: {releaseError}");
                    }
#pragma warning restore GHCP001
                }
            }
            else
            {
                ReleaseChatCancellation(chat.Id, cancel: true);
                MarkRuntimeTerminal(runtime);
                chat.PauseNeedsContinuation = true;
                SetChatPaused(chat, true);
                if (CurrentChat?.Id == chat.Id)
                    ApplyDisplayedRuntimeState(runtime);
            }
            PauseError = Loc.Get(paused ? "Chat_PauseFailed" : "Chat_ResumeFailed", error.Message);
            runtime.StatusText = PauseError;
            if (CurrentChat?.Id == chat.Id)
                StatusText = PauseError;
            return PauseError;
        }
    }

    internal void SetChatPaused(Chat chat, bool paused)
    {
        chat.IsPaused = paused;
        if (_runtimeStates.TryGetValue(chat.Id, out var runtime))
        {
            runtime.PauseGate.SetPaused(paused);
            RefreshChatPauseState(runtime);
        }
        else
            chat.IsPausePending = false;

        if (_queuedBusySendPrompts.TryGetValue(chat.Id, out var queued))
        {
            foreach (var message in queued)
            {
                var canSendNow = !paused && IsChatRuntimeActive(chat.Id);
                message.CanSendNowWhenQueued = canSendNow;
                if (ResolveQueuedViewModel(message) is { } viewModel)
                    viewModel.CanSendNowWhenQueued = canSendNow;
            }
        }
        QueueSaveChatIndex(chat);
        if (!paused)
            ScheduleQueuedBusySendDrain(chat.Id);
    }

    private void RefreshChatPauseState(ChatRuntimeState runtime)
    {
        if (_isDisposed || runtime.Chat is not { } chat)
            return;

        chat.IsPausePending = chat.IsPaused
                              && (runtime.StopOperation is { IsCompleted: false }
                                  || (runtime.IsSessionActive && !runtime.PauseGate.IsWaiting));
        if (CurrentChat?.Id == chat.Id)
            NotifyChatPausePropertiesChanged();
    }

    private void NotifyChatPausePropertiesChanged()
    {
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(IsPausePending));
        OnPropertyChanged(nameof(CanPauseChat));
        OnPropertyChanged(nameof(IsComposerBusy));
        OnPropertyChanged(nameof(IsTranscriptStreaming));
        OnPropertyChanged(nameof(PauseStatusTitle));
        OnPropertyChanged(nameof(PauseStatusDescription));
        OnPropertyChanged(nameof(ComposerPlaceholder));
        OnPropertyChanged(nameof(HasBackgroundActivity));
        OnPropertyChanged(nameof(ShowInfoStrip));
        NotifyContextActionAvailabilityChanged();
        if (IsPaused)
            _transcriptBuilder.HideTypingIndicator();
        else if (IsBusy)
            _transcriptBuilder.ShowTypingIndicator(StatusText);
    }

    private void TrackChatPauseGate(ChatRuntimeState runtime)
    {
        runtime.PauseGate.SetPaused(runtime.Chat?.IsPaused == true);
        runtime.PauseGate.WaitingChanged += () => Dispatcher.UIThread.Post(() =>
        {
            if (runtime.Chat is { } chat
                && _runtimeStates.GetValueOrDefault(chat.Id) == runtime)
                RefreshChatPauseState(runtime);
        });
    }
}
