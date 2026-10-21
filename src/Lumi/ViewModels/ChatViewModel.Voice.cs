using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Services;

namespace Lumi.ViewModels;

/// <summary>
/// Voice typing in the composer. The first time, Lumi asks before downloading the on-device speech model; after that
/// the microphone opens at once, the words heard so far show while the user speaks, and each phrase lands in the
/// draft at a pause. Sending finishes dictation first, so nothing said is lost.
/// </summary>
public partial class ChatViewModel
{
    private enum VoicePhase { Idle, Setup, Downloading, Starting, Listening, Finishing, Problem }

    private static readonly TimeSpan MicrophoneWaitHintDelay = TimeSpan.FromSeconds(2);
    private readonly VoiceInputService _voiceService;
    private VoicePhase _voicePhase;
    private int _voiceInputVersion;
    private Action<VoiceInputState>? _voiceStateHandler;
    private Action<string>? _voiceResultHandler;
    private Action<string>? _voicePreviewHandler;
    private Action<double>? _voiceLevelHandler;
    private Action<VoiceInputError>? _voiceErrorHandler;
    // The draft before this dictation, and after its last phrase, so discarding can put it back.
    private string _voiceDraftBefore = "";
    private string? _voiceDraftAfter;
    private CancellationTokenSource? _voiceModelDownloadCts;
    private bool _startVoiceWhenModelReady;
    private VoiceInputErrorKind _voiceProblemKind;
    // The speech model this computer uses, known once voice typing was first used.
    private VoiceModelStore? _voiceModel;
    private VoiceLanguageChoices? _voiceLanguageChoices;

    public bool IsVoiceAvailable => _voiceService.IsAvailable;

    /// <summary>The composer is dictating: starting the microphone, listening, or finishing the last words.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ComposerPlaceholder))]
    private bool _isRecording;

    [ObservableProperty] private bool _isVoiceSetupVisible;
    [ObservableProperty] private bool _isVoiceDownloadVisible;
    [ObservableProperty] private bool _isVoiceProblemVisible;
    /// <summary>A short note in the voice bar while starting or finishing.</summary>
    [ObservableProperty] private string _voiceStatus = "";
    /// <summary>The words heard so far in the phrase being spoken.</summary>
    [ObservableProperty] private string _voicePreview = "";
    [ObservableProperty] private double _voiceLevel;
    [ObservableProperty] private double _voiceDownloadPercent;
    [ObservableProperty] private string _voiceDownloadText = "";
    [ObservableProperty] private string _voiceProblemTitle = "";
    [ObservableProperty] private string _voiceProblemMessage = "";
    [ObservableProperty] private string _voiceProblemDetail = "";
    [ObservableProperty] private bool _isVoiceMicrophoneProblem;
    [ObservableProperty] private bool _isVoiceNoSpeech;
    [ObservableProperty] private bool _canOpenMicrophoneSettings;
    /// <summary>What setting voice typing up involves, with the size of this computer's model.</summary>
    [ObservableProperty] private string _voiceSetupMessage = "";

    /// <summary>The languages the voice bar offers first; every other one is in <see cref="MoreVoiceLanguages"/>.</summary>
    public IReadOnlyList<VoiceLanguageOption> VoiceLanguages => VoiceLanguageChoices.Suggested;
    public IReadOnlyList<VoiceLanguageOption> MoreVoiceLanguages => VoiceLanguageChoices.More;

    /// <summary>
    /// The language dictation listens for, remembered for every chat. Chosen while dictating, it applies from the next
    /// words on.
    /// </summary>
    public VoiceLanguageOption SelectedVoiceLanguage
    {
        get => VoiceLanguageChoices.Selected;
        set
        {
            if (value is null || value == VoiceLanguageChoices.Selected)
                return;

            _dataStore.Data.Settings.VoiceLanguage = value.Code;
            _dataStore.Save();
            _voiceService.SetLanguage(value.Code);
            // A language picked from the full list joins the ones offered first.
            RefreshVoiceLanguages();
        }
    }

    private VoiceLanguageChoices VoiceLanguageChoices => _voiceLanguageChoices ??= BuildVoiceLanguageChoices();

    private VoiceLanguageChoices BuildVoiceLanguageChoices()
        => VoiceLanguageCatalog.Build(
            Loc.Voice_LanguageAuto, _dataStore.Data.Settings.VoiceLanguage, VoiceLanguageCatalog.UserLanguages(Loc.Culture));

    private void RefreshVoiceLanguages()
    {
        _voiceLanguageChoices = BuildVoiceLanguageChoices();
        OnPropertyChanged(nameof(VoiceLanguages));
        OnPropertyChanged(nameof(MoreVoiceLanguages));
        OnPropertyChanged(nameof(SelectedVoiceLanguage));
    }

    private static string? MicrophoneSettingsUri
        => OperatingSystem.IsWindows() ? "ms-settings:privacy-microphone"
            : OperatingSystem.IsMacOS() ? "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone"
            : null;

    /// <summary>The composer's dictation button: starts, finishes, or answers whatever voice typing is showing.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleVoice()
    {
        if (_isDisposed || !_voiceService.IsAvailable)
            return;

        switch (_voicePhase)
        {
            case VoicePhase.Starting or VoicePhase.Listening:
                await FinishVoiceAsync();
                return;
            case VoicePhase.Finishing:
                return;
            case VoicePhase.Setup:
                SetVoicePhase(VoicePhase.Idle);
                return;
            case VoicePhase.Downloading:
                // The download carries on; listening just won't start by itself when it's done.
                _startVoiceWhenModelReady = false;
                SetVoicePhase(VoicePhase.Idle);
                return;
        }

        await BeginVoiceAsync();
    }

    /// <summary>"Download and start": the user agreed to the one-time model download.</summary>
    [RelayCommand]
    private async Task DownloadVoiceModel()
    {
        var model = _voiceModel ??= await _voiceService.GetModelAsync();
        if (_isDisposed)
            return;

        _startVoiceWhenModelReady = true;
        // The card shows first: a download that ends at once (cached, or failing) then finds it in place.
        SetVoicePhase(VoicePhase.Downloading);
        if (_voiceModelDownloadCts is null)
        {
            ShowVoiceDownloadProgress(0, model.DownloadSize);
            _voiceModelDownloadCts = new CancellationTokenSource();
            _ = RunVoiceModelDownloadAsync(model, _voiceModelDownloadCts);
        }
    }

    /// <summary>"Not now", "Cancel" or close on a voice typing card. Cancelling the progress card stops the download.</summary>
    [RelayCommand]
    private void DismissVoice()
    {
        if (_voicePhase == VoicePhase.Downloading)
            _voiceModelDownloadCts?.Cancel();
        _startVoiceWhenModelReady = false;
        if (_voicePhase is VoicePhase.Setup or VoicePhase.Downloading or VoicePhase.Problem)
            SetVoicePhase(VoicePhase.Idle);
    }

    [RelayCommand]
    private Task RetryVoice()
        => _voiceProblemKind == VoiceInputErrorKind.Download ? DownloadVoiceModel() : BeginVoiceAsync();

    /// <summary>Discards this dictation (the voice bar's ✕ or Escape): stops listening and puts the draft back.</summary>
    [RelayCommand]
    private void DiscardVoice()
    {
        // Only a draft nobody edited since is put back; otherwise what was typed stays.
        var restore = _voiceDraftAfter is not null && string.Equals(PromptText ?? "", _voiceDraftAfter, StringComparison.Ordinal);
        var before = _voiceDraftBefore;
        CancelVoice();
        if (restore)
            PromptText = before;
    }

    [RelayCommand]
    private void OpenMicrophoneSettings()
    {
        if (MicrophoneSettingsUri is not { } uri)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Voice] Could not open microphone settings: {ex.Message}");
        }
    }

    private async Task BeginVoiceAsync()
    {
        var model = _voiceModel ??= await _voiceService.GetModelAsync();
        if (_isDisposed)
            return;

        if (model.IsInstalled)
        {
            await StartVoiceSessionAsync();
            return;
        }

        if (_voiceModelDownloadCts is not null)
        {
            // Still downloading from an earlier yes: show its progress and listen once it's done.
            _startVoiceWhenModelReady = true;
            SetVoicePhase(VoicePhase.Downloading);
            return;
        }

        ShowVoiceSetup();
    }

    private void ShowVoiceSetup()
    {
        VoiceSetupMessage = string.Format(Loc.Voice_SetupBody, FormatMegabytes(_voiceModel?.DownloadSize ?? 0));
        SetVoicePhase(VoicePhase.Setup);
    }

    private async Task RunVoiceModelDownloadAsync(VoiceModelStore model, CancellationTokenSource cancellation)
    {
        Exception? failure = null;
        try
        {
            await model.DownloadAsync(
                new Progress<long>(received => ShowVoiceDownloadProgress(received, model.DownloadSize)), cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            failure = ex;
            Debug.WriteLine($"[Voice] Model download failed: {ex}");
        }

        var cancelled = cancellation.IsCancellationRequested;
        if (ReferenceEquals(_voiceModelDownloadCts, cancellation))
            _voiceModelDownloadCts = null;
        cancellation.Dispose();

        var wanted = !_isDisposed && !cancelled && _startVoiceWhenModelReady && _voicePhase == VoicePhase.Downloading;
        _startVoiceWhenModelReady = false;
        if (!wanted)
            return;
        if (failure is not null)
        {
            ShowVoiceProblem(VoiceInputErrorKind.Download, failure.Message);
            return;
        }

        await StartVoiceSessionAsync();
    }

    private async Task StartVoiceSessionAsync()
    {
        var version = ++_voiceInputVersion;
        DetachVoiceHandlers();
        // Another chat may have chosen the language since this one last listened.
        if (_voiceLanguageChoices?.Selected.Code != _dataStore.Data.Settings.VoiceLanguage)
            RefreshVoiceLanguages();
        _voiceDraftBefore = PromptText ?? "";
        _voiceDraftAfter = null;
        SetVoicePhase(VoicePhase.Starting);
        FocusComposerRequested?.Invoke();
        _ = ShowMicrophoneWaitHintAsync(version);

        // A dictation cancelled a moment ago may still be releasing the microphone. Finishing or leaving meanwhile
        // means this one never starts, so the microphone can't open without its voice bar.
        await _voiceService.CancelAsync();
        if (_isDisposed || version != _voiceInputVersion || _voicePhase != VoicePhase.Starting)
            return;

        _voiceStateHandler = state => DispatchVoice(version, () => OnVoiceStateChanged(state));
        _voiceResultHandler = text => DispatchVoice(version, () => AppendVoiceResult(text));
        _voicePreviewHandler = text => DispatchVoice(version, () => VoicePreview = text);
        _voiceLevelHandler = level => DispatchVoice(version, () => VoiceLevel = level);
        _voiceErrorHandler = error => DispatchVoice(version, () => OnVoiceError(error));
        _voiceService.StateChanged += _voiceStateHandler;
        _voiceService.ResultGenerated += _voiceResultHandler;
        _voiceService.PreviewChanged += _voicePreviewHandler;
        _voiceService.LevelChanged += _voiceLevelHandler;
        _voiceService.Error += _voiceErrorHandler;
        await _voiceService.StartAsync(SelectedVoiceLanguage.Code);
    }

    private async Task ShowMicrophoneWaitHintAsync(int version)
    {
        await Task.Delay(MicrophoneWaitHintDelay);
        if (version == _voiceInputVersion && _voicePhase == VoicePhase.Starting)
            VoiceStatus = Loc.Voice_WaitingForMicrophone;
    }

    /// <summary>Finishes dictation and waits until its last words are in the draft.</summary>
    private async Task FinishVoiceAsync()
    {
        var version = _voiceInputVersion;
        SetVoicePhase(VoicePhase.Finishing);
        await _voiceService.StopAsync();
        // The last phrase arrives as posted work; let it land before anyone reads the draft.
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Normal);
        if (version == _voiceInputVersion && _voicePhase == VoicePhase.Finishing)
            SetVoicePhase(VoicePhase.Idle);
    }

    /// <summary>
    /// Sending takes the words still being heard along, and puts any voice card away. Returns false when the user
    /// moved on while the last words were finishing (another chat, an edit, a discard): nothing should be sent then.
    /// </summary>
    private async Task<bool> FinishVoiceBeforeSendAsync()
    {
        if (!IsRecording)
        {
            CancelVoice();
            return true;
        }

        var chat = CurrentChat;
        var editing = _editingUserMessage;
        var version = _voiceInputVersion;
        await FinishVoiceAsync();
        return !_isDisposed
               && version == _voiceInputVersion
               && ReferenceEquals(chat, CurrentChat)
               && ReferenceEquals(editing, _editingUserMessage);
    }

    private void DispatchVoice(int version, Action action)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_isDisposed && version == _voiceInputVersion)
                action();
        }, DispatcherPriority.Normal);
    }

    private void OnVoiceStateChanged(VoiceInputState state)
    {
        switch (state)
        {
            case VoiceInputState.Listening when _voicePhase == VoicePhase.Starting:
                SetVoicePhase(VoicePhase.Listening);
                break;
            case VoiceInputState.Finishing when _voicePhase is VoicePhase.Starting or VoicePhase.Listening:
                SetVoicePhase(VoicePhase.Finishing);
                break;
            case VoiceInputState.Idle:
                DetachVoiceHandlers();
                if (_voicePhase is VoicePhase.Starting or VoicePhase.Listening or VoicePhase.Finishing)
                    SetVoicePhase(VoicePhase.Idle);
                break;
        }
    }

    private void AppendVoiceResult(string text)
    {
        var draft = PromptText ?? "";
        PromptText = draft + (draft.Length > 0 && !char.IsWhiteSpace(draft[^1]) ? " " : "") + text;
        _voiceDraftAfter = PromptText;
        VoicePreview = "";
    }

    private void OnVoiceError(VoiceInputError error)
    {
        // A damaged model was removed: setting voice typing up again is the fix.
        if (error.Kind == VoiceInputErrorKind.Model)
            ShowVoiceSetup();
        else
            ShowVoiceProblem(error.Kind, error.Message);
    }

    private void ShowVoiceProblem(VoiceInputErrorKind kind, string detail = "")
    {
        _voiceProblemKind = kind;
        var microphoneHint = OperatingSystem.IsWindows() ? Loc.Voice_MicrophoneHintWindows
            : OperatingSystem.IsMacOS() ? Loc.Voice_MicrophoneHintMac
            : Loc.Voice_MicrophoneHintLinux;
        (VoiceProblemTitle, VoiceProblemMessage) = kind switch
        {
            VoiceInputErrorKind.Microphone => (Loc.Voice_MicrophoneTitle, microphoneHint),
            VoiceInputErrorKind.Silent => (Loc.Voice_SilentTitle, $"{Loc.Voice_SilentBody} {microphoneHint}"),
            VoiceInputErrorKind.NoSpeech => (Loc.Voice_NoSpeechTitle, Loc.Voice_NoSpeechBody),
            VoiceInputErrorKind.Download => (Loc.Voice_DownloadFailedTitle, Loc.Voice_DownloadFailedBody),
            _ => (Loc.Voice_FailedTitle, Loc.Voice_FailedBody),
        };
        VoiceProblemDetail = kind is VoiceInputErrorKind.NoSpeech or VoiceInputErrorKind.Silent ? "" : detail;
        IsVoiceMicrophoneProblem = kind is VoiceInputErrorKind.Microphone or VoiceInputErrorKind.Silent;
        IsVoiceNoSpeech = kind == VoiceInputErrorKind.NoSpeech;
        CanOpenMicrophoneSettings = IsVoiceMicrophoneProblem && MicrophoneSettingsUri is not null;
        SetVoicePhase(VoicePhase.Problem);
    }

    private void ShowVoiceDownloadProgress(long received, long size)
    {
        var total = Math.Max(1, size);
        VoiceDownloadPercent = Math.Clamp(received * 100.0 / total, 0, 100);
        VoiceDownloadText = string.Format(Loc.Voice_DownloadProgress, FormatMegabytes(received), FormatMegabytes(total));
    }

    private static string FormatMegabytes(long bytes)
        => (bytes / 1_000_000.0).ToString("0", CultureInfo.CurrentCulture);

    private void SetVoicePhase(VoicePhase phase)
    {
        _voicePhase = phase;
        IsVoiceSetupVisible = phase == VoicePhase.Setup;
        IsVoiceDownloadVisible = phase == VoicePhase.Downloading;
        IsVoiceProblemVisible = phase == VoicePhase.Problem;
        IsRecording = phase is VoicePhase.Starting or VoicePhase.Listening or VoicePhase.Finishing;
        VoiceStatus = phase switch
        {
            VoicePhase.Starting => Loc.Voice_Starting,
            VoicePhase.Finishing => Loc.Voice_Finishing,
            _ => "",
        };
        if (phase != VoicePhase.Listening)
            VoiceLevel = 0;
        // The words still being finished stay in view until they land.
        if (!IsRecording)
            VoicePreview = "";
    }

    partial void OnPromptTextChanged(string? value)
    {
        // Typing instead answers a voice card: the user moved on.
        if (_voicePhase is VoicePhase.Setup or VoicePhase.Problem)
            SetVoicePhase(VoicePhase.Idle);
    }

    /// <summary>
    /// Stops dictation without transcribing more, keeping what already landed in the draft, and puts any voice card
    /// away. A model download the user agreed to carries on.
    /// </summary>
    private void CancelVoice()
    {
        _voiceInputVersion++;
        DetachVoiceHandlers();
        _ = _voiceService.CancelAsync();
        _startVoiceWhenModelReady = false;
        _voiceDraftAfter = null;
        SetVoicePhase(VoicePhase.Idle);
    }

    private void DetachVoiceHandlers()
    {
        _voiceService.StateChanged -= _voiceStateHandler;
        _voiceService.ResultGenerated -= _voiceResultHandler;
        _voiceService.PreviewChanged -= _voicePreviewHandler;
        _voiceService.LevelChanged -= _voiceLevelHandler;
        _voiceService.Error -= _voiceErrorHandler;
        _voiceStateHandler = null;
        _voiceResultHandler = null;
        _voicePreviewHandler = null;
        _voiceLevelHandler = null;
        _voiceErrorHandler = null;
    }

    private void DisposeVoice()
    {
        // A model download the user agreed to carries on: it is for every chat, not just this one.
        DetachVoiceHandlers();
        _voiceService.Dispose();
    }

    /// <summary>Also puts away first-use setup and pending transcription when leaving the composer.</summary>
    public void StopVoiceIfRecording() => CancelVoice();
}
