using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Lumi.Localization;

namespace Lumi.Services;

internal enum VoiceInputState
{
    Idle,
    /// <summary>Opening the microphone; the speech model loads alongside.</summary>
    Starting,
    Listening,
    /// <summary>The microphone is closed and the last words are being transcribed.</summary>
    Finishing,
}

/// <summary>What stopped dictation, in terms a person can act on.</summary>
internal enum VoiceInputErrorKind
{
    /// <summary>The microphone couldn't be opened, or sent no audio.</summary>
    Microphone,
    /// <summary>The microphone sent only digital silence: muted, or blocked by the system.</summary>
    Silent,
    /// <summary>Sound arrived, but no words were recognized.</summary>
    NoSpeech,
    /// <summary>The speech model couldn't be downloaded.</summary>
    Download,
    /// <summary>The speech model is missing or damaged and has to be set up again.</summary>
    Model,
    Failed,
}

internal sealed record VoiceInputError(VoiceInputErrorKind Kind, string Message = "");

internal sealed class VoiceInputException(VoiceInputErrorKind kind, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public VoiceInputErrorKind Kind { get; } = kind;
}

internal delegate void VoiceAudioCallback(ReadOnlySpan<float> samples);

/// <summary>Words recognized in some audio, and the language they were heard in.</summary>
internal readonly record struct VoiceTranscript(string Text, string? Language);

internal interface IVoiceInputBackend : IAsyncDisposable
{
    /// <summary>Opens the microphone and starts capture; completes once audio arrives.</summary>
    Task StartCaptureAsync(VoiceAudioCallback onAudio, CancellationToken cancellationToken);
    void StopCapture();

    /// <summary>Loads the speech model. It runs alongside <see cref="StartCaptureAsync"/>, so listening never waits for it.</summary>
    Task LoadModelAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Transcribes audio, detecting its language while <paramref name="language"/> is null. A preview is quick and
    /// provisional: it looks at no more than <see cref="VoiceInputService.PreviewMaxSamples"/> of the newest audio.
    /// </summary>
    Task<VoiceTranscript> TranscribeAsync(float[] samples, string? language, bool preview, CancellationToken cancellationToken);
}

/// <summary>
/// Owns one cancellable, on-device dictation session. The microphone opens at once while the model loads; words are
/// previewed as they are spoken and final at each pause.
/// </summary>
internal sealed class VoiceInputService : IDisposable
{
    // Previews start once a phrase holds this much audio, decode at most its newest ten seconds, and refresh when it
    // has grown a little, no faster than the interval or one and a half decodes.
    internal const int PreviewMaxSamples = VoiceAudioBuffer.SampleRate * 10;
    private const int PreviewMinSamples = VoiceAudioBuffer.SampleRate * 7 / 10;
    private const int PreviewMinGrowth = VoiceAudioBuffer.SampleRate * 3 / 10;
    private static readonly TimeSpan PreviewInterval = TimeSpan.FromMilliseconds(500);
    // A phrase holding about two seconds besides its closing pause names its language reliably, so the rest of the
    // dictation keeps it: no detection pass per decode, and no short phrase misheard as another language.
    private const int LanguageLockSamples = VoiceAudioBuffer.SampleRate * 2 + VoiceAudioBuffer.PauseSamples;

    private readonly object _gate = new();
    private readonly Func<VoiceModelStore, IVoiceInputBackend> _createBackend;
    private readonly Func<Task<VoiceModelStore>> _chooseModel;
    private Session? _session;
    private volatile VoiceInputState _state;
    private bool _disposed;

    public event Action<VoiceInputState>? StateChanged;
    /// <summary>A finished phrase.</summary>
    public event Action<string>? ResultGenerated;
    /// <summary>The words heard so far in the phrase in progress, or empty when none are pending.</summary>
    public event Action<string>? PreviewChanged;
    /// <summary>The microphone level from 0 to 1, raised on the audio thread.</summary>
    public event Action<double>? LevelChanged;
    public event Action<VoiceInputError>? Error;

    public VoiceInputState State => _state;
    public bool IsAvailable => IsSupportedOnThisComputer;

    /// <summary>Dictation runs on this operating system and processor.</summary>
    internal static bool IsSupportedOnThisComputer => IsSupportedPlatform(
        OperatingSystem.IsWindows() ? OSPlatform.Windows
        : OperatingSystem.IsLinux() ? OSPlatform.Linux
        : OperatingSystem.IsMacOS() ? OSPlatform.OSX : default,
        RuntimeInformation.ProcessArchitecture);

    public VoiceInputService()
        : this(static model => new LocalVoiceInputBackend(model), VoiceModelStore.RecommendedAsync) { }

    internal VoiceInputService(Func<IVoiceInputBackend> createBackend, VoiceModelStore model)
        : this(_ => createBackend(), () => Task.FromResult(model)) { }

    internal VoiceInputService(Func<VoiceModelStore, IVoiceInputBackend> createBackend, Func<Task<VoiceModelStore>> chooseModel)
    {
        _createBackend = createBackend;
        _chooseModel = chooseModel;
    }

    internal static bool IsSupportedPlatform(OSPlatform platform, Architecture architecture)
        => (platform == OSPlatform.Windows || platform == OSPlatform.Linux || platform == OSPlatform.OSX)
           && architecture is Architecture.X64 or Architecture.Arm64;

    /// <summary>The speech model dictation uses on this computer, installed or not yet.</summary>
    public Task<VoiceModelStore> GetModelAsync() => _chooseModel();

    /// <summary>
    /// Starts listening for <paramref name="language"/> (a Whisper language code; null or empty detects it); completes
    /// once audio arrives, or when the attempt ends.
    /// </summary>
    public Task StartAsync(string? language = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is not null)
                return _session.Started.Task;
            if (!IsAvailable)
            {
                Error?.Invoke(new VoiceInputError(VoiceInputErrorKind.Failed, Loc.Voice_UnsupportedPlatform));
                return Task.CompletedTask;
            }

            var session = new Session { Language = NormalizeLanguage(language) };
            _session = session;
            session.Completion = Task.Run(() => RunAsync(session));
            return session.Started.Task;
        }
    }

    /// <summary>
    /// Listens for another language from the next words on; null or empty detects it afresh, rather than keeping a
    /// language detected earlier in this dictation.
    /// </summary>
    public void SetLanguage(string? language)
    {
        lock (_gate)
        {
            if (_session is { } session)
            {
                session.Language = NormalizeLanguage(language);
                session.DetectedLanguage = null;
            }
        }
    }

    private static string? NormalizeLanguage(string? language)
        => string.IsNullOrWhiteSpace(language) ? null : language.Trim().ToLowerInvariant();

    /// <summary>Finishes: transcribes what was heard, then completes. Before any audio arrived it just cancels.</summary>
    public Task StopAsync()
    {
        lock (_gate)
        {
            if (_session is not { } session)
                return Task.CompletedTask;
            if (_state == VoiceInputState.Listening)
                session.StopRequested.TrySetResult();
            else if (_state != VoiceInputState.Finishing)
                session.Cancellation.Cancel();
            return session.Completion;
        }
    }

    /// <summary>Stops without transcribing anything more; completes once the microphone is released.</summary>
    public Task CancelAsync()
    {
        lock (_gate)
        {
            _session?.Cancellation.Cancel();
            return _session?.Completion ?? Task.CompletedTask;
        }
    }

    private async Task RunAsync(Session session)
    {
        var token = session.Cancellation.Token;
        VoiceModelStore? model = null;
        try
        {
            SetState(session, VoiceInputState.Starting);
            model = await _chooseModel();
            await using var backend = _createBackend(model);
            var modelLoad = backend.LoadModelAsync(token);
            var processing = ProcessAsync(session, backend, modelLoad, token);
            try
            {
                await backend.StartCaptureAsync(samples => OnAudio(session, samples), token);
                token.ThrowIfCancellationRequested();
                SetState(session, VoiceInputState.Listening);
                session.Started.TrySetResult();

                // Processing only ends early when it fails.
                if (await Task.WhenAny(session.StopRequested.Task, processing).WaitAsync(token) == processing)
                    await processing;

                backend.StopCapture();
                SetState(session, VoiceInputState.Finishing);
                if (session.Buffer.Drain() is { } lastPhrase)
                    session.Phrases.Writer.TryWrite(lastPhrase);
                session.Phrases.Writer.TryComplete();
                await processing;
                token.ThrowIfCancellationRequested();
                if (session.ResultCount == 0)
                    RaiseError(session, new VoiceInputError(
                        session.Buffer.HeardSignal ? VoiceInputErrorKind.NoSpeech : VoiceInputErrorKind.Silent));
            }
            finally
            {
                // Stop native callbacks before the decoder goes, and let a model still loading finish before disposal.
                // Failures here were either reported already or came from cancelling.
                try { backend.StopCapture(); }
                finally
                {
                    session.Phrases.Writer.TryComplete();
                    session.Cancellation.Cancel();
                    try { await processing; } catch { }
                    try { await modelLoad; } catch { }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Voice] {ex}");
            var error = ex is VoiceInputException voice
                ? new VoiceInputError(voice.Kind, voice.Message)
                : new VoiceInputError(VoiceInputErrorKind.Failed, ex.Message);
            if (error.Kind == VoiceInputErrorKind.Model)
                model?.Discard();
            RaiseError(session, error);
        }
        finally
        {
            lock (_gate)
            {
                _state = VoiceInputState.Idle;
                if (!_disposed)
                    StateChanged?.Invoke(VoiceInputState.Idle);
                _session = null;
            }
            session.Cancellation.Dispose();
            session.Started.TrySetResult();
        }
    }

    private void OnAudio(Session session, ReadOnlySpan<float> samples)
    {
        if (session.Cancellation.IsCancellationRequested)
            return;
        if (session.Buffer.Append(samples, out var level) is { } phrase)
            session.Phrases.Writer.TryWrite(phrase);
        if (CanPublish(session))
            LevelChanged?.Invoke(level);
    }

    private async Task ProcessAsync(Session session, IVoiceInputBackend backend, Task modelLoad, CancellationToken token)
    {
        // Audio keeps collecting while the model loads, so nothing said meanwhile is lost.
        await modelLoad;
        var reader = session.Phrases.Reader;
        var previewInterval = PreviewInterval;
        var nextPreview = Stopwatch.GetTimestamp();
        var previewPhrase = -1;
        var previewLength = 0;
        var showingPreview = false;
        Task<bool>? waiting = null;

        while (true)
        {
            // A finished phrase always comes before another preview.
            if (reader.TryRead(out var phrase))
            {
                // Read once: the user may choose another language while this phrase is decoded, and a language the
                // phrase was only heard in because it was chosen must not become the detected one.
                var language = session.Listening;
                var transcript = await backend.TranscribeAsync(phrase, language, preview: false, token);
                token.ThrowIfCancellationRequested();
                if (language is null && session.Language is null && phrase.Length >= LanguageLockSamples
                    && transcript.Text.Length > 0)
                    session.DetectedLanguage = transcript.Language;
                if (transcript.Text.Length > 0 && CanPublish(session))
                {
                    session.ResultCount++;
                    ResultGenerated?.Invoke(transcript.Text);
                }
                if (showingPreview && CanPublish(session))
                    PreviewChanged?.Invoke("");
                showingPreview = false;
                continue;
            }

            waiting ??= reader.WaitToReadAsync(token).AsTask();
            if (!waiting.IsCompleted)
            {
                var untilPreview = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), nextPreview);
                if (untilPreview > TimeSpan.Zero)
                    await Task.WhenAny(waiting, Task.Delay(untilPreview, token));
                token.ThrowIfCancellationRequested();
            }
            if (waiting.IsCompleted)
            {
                var more = await waiting;
                waiting = null;
                if (!more)
                    return;
                continue;
            }

            nextPreview = Stopwatch.GetTimestamp() + (long)(previewInterval.TotalSeconds * Stopwatch.Frequency);
            if (session.Buffer.Snapshot(PreviewMinSamples, PreviewMaxSamples) is not { } snapshot
                || snapshot.Phrase == previewPhrase && snapshot.Length - previewLength < PreviewMinGrowth)
                continue;

            var started = Stopwatch.GetTimestamp();
            var preview = await backend.TranscribeAsync(snapshot.Samples, session.Listening, preview: true, token);
            token.ThrowIfCancellationRequested();
            var decodeTime = Stopwatch.GetElapsedTime(started);
            previewInterval = decodeTime * 1.5 > PreviewInterval ? decodeTime * 1.5 : PreviewInterval;
            previewPhrase = snapshot.Phrase;
            previewLength = snapshot.Length;
            // A phrase that ended meanwhile is about to arrive whole; its preview would only flicker.
            if (preview.Text.Length > 0 && session.Buffer.IsCurrent(snapshot.Phrase) && !reader.TryPeek(out _)
                && CanPublish(session))
            {
                showingPreview = true;
                PreviewChanged?.Invoke(preview.Text);
            }
        }
    }

    private void RaiseError(Session session, VoiceInputError error)
    {
        lock (_gate)
        {
            if (!_disposed && ReferenceEquals(_session, session))
                Error?.Invoke(error);
        }
    }

    private bool CanPublish(Session session)
    {
        lock (_gate)
            return !_disposed && ReferenceEquals(_session, session) && !session.Cancellation.IsCancellationRequested;
    }

    private void SetState(Session session, VoiceInputState state)
    {
        if (!CanPublish(session))
            return;
        _state = state;
        StateChanged?.Invoke(state);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _session?.Cancellation.Cancel();
        }
    }

    private sealed class Session
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StopRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Channel<float[]> Phrases { get; } = Channel.CreateUnbounded<float[]>(
            new UnboundedChannelOptions { SingleReader = true });
        public VoiceAudioBuffer Buffer { get; } = new();
        public Task Completion { get; set; } = Task.CompletedTask;
        public int ResultCount { get; set; }
        /// <summary>The language the user chose, which the UI thread may change mid-dictation; null detects it.</summary>
        public volatile string? Language;
        /// <summary>The language detected from the first long phrase, kept until the user chooses a language.</summary>
        public volatile string? DetectedLanguage;
        public string? Listening => Language ?? DetectedLanguage;
    }
}
