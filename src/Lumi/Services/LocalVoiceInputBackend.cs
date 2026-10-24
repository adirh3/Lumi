using System.Runtime.InteropServices;
using System.Text;
using Lumi.Localization;
using Silk.NET.Core.Contexts;
using Silk.NET.SDL;
using Whisper.net;

namespace Lumi.Services;

internal sealed class LocalVoiceInputBackend : IVoiceInputBackend
{
    private static readonly TimeSpan FirstAudioTimeout = TimeSpan.FromSeconds(10);
    private const int FullAudioContext = 1500;
    private const int EncoderFrameSamples = 320;
    // A trimmed window covers the phrase and a little more, in steps that let processors be reused.
    private const int AudioContextMargin = 64;
    private static readonly int DecoderThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);

    private readonly string _modelPath;
    private readonly string? _speechGatePath;
    private readonly bool _trimAudioWindow;
    private Sdl? _audio;
    private uint _device;
    private bool _audioInitialized;
    private PfnAudioCallback _captureCallback;
    private bool _callbackAllocated;
    private VoiceAudioCallback? _onAudio;
    private readonly TaskCompletionSource _firstAudio = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WhisperFactory? _factory;
    private WhisperVadFactory? _speechGateFactory;
    private WhisperVadProcessor? _speechGate;
    private ProcessorSlot _final;
    private ProcessorSlot _preview;

    public LocalVoiceInputBackend(VoiceModelStore model)
    {
        _modelPath = model.ModelPath;
        _speechGatePath = model.SpeechGatePath;
        _trimAudioWindow = model.TrimAudioWindow;
    }

    public Task LoadModelAsync(CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            VoiceHardware.UseRunnableRuntimes();
            _factory = LoadWhisper();
            if (_speechGatePath is not null)
                LoadSpeechGate(_speechGatePath);
        }, cancellationToken);

    private WhisperFactory LoadWhisper()
    {
        if (!File.Exists(_modelPath))
            throw new VoiceInputException(VoiceInputErrorKind.Model, Loc.Voice_ModelNotInstalled);
        var factory = WhisperFactory.FromPath(_modelPath);
        try
        {
            // A file Whisper can't read only fails once a processor is built; find out now, alongside capture, so a
            // damaged model is set up again rather than failing every dictation.
            factory.CreateBuilder();
            return factory;
        }
        catch (WhisperModelLoadException ex)
        {
            factory.Dispose();
            throw new VoiceInputException(VoiceInputErrorKind.Model, Loc.Voice_ModelLoadFailed, ex);
        }
    }

    private void LoadSpeechGate(string path)
    {
        if (!File.Exists(path))
            throw new VoiceInputException(VoiceInputErrorKind.Model, Loc.Voice_ModelNotInstalled);
        try
        {
            _speechGateFactory = WhisperVadFactory.FromPath(path);
            _speechGate = _speechGateFactory.CreateBuilder()
                .WithUseGpu(false)
                .WithThreshold(0.5f)
                .WithMinSpeechDuration(TimeSpan.FromMilliseconds(250))
                .Build();
        }
        catch (WhisperModelLoadException ex)
        {
            _speechGateFactory?.Dispose();
            _speechGateFactory = null;
            throw new VoiceInputException(VoiceInputErrorKind.Model, Loc.Voice_ModelLoadFailed, ex);
        }
    }

    public async Task StartCaptureAsync(VoiceAudioCallback onAudio, CancellationToken cancellationToken)
    {
        InitializeMicrophone();
        cancellationToken.ThrowIfCancellationRequested();
        _onAudio = onAudio;
        _audio!.PauseAudioDevice(_device, 0);
        try
        {
            await _firstAudio.Task.WaitAsync(FirstAudioTimeout, cancellationToken);
        }
        catch (TimeoutException ex)
        {
            throw new VoiceInputException(VoiceInputErrorKind.Microphone, Loc.Voice_MicrophoneNoAudio, ex);
        }
    }

    private unsafe void InitializeMicrophone()
    {
        _audio = new Sdl(new DefaultNativeContext(GetAudioLibraryPath()));
        if (_audio.InitSubSystem(Sdl.InitAudio) != 0)
            throw new VoiceInputException(VoiceInputErrorKind.Microphone, string.Format(Loc.Voice_AudioStartFailed, _audio.GetErrorS()));
        _audioInitialized = true;
        _captureCallback = PfnAudioCallback.From(OnAudioProcessed);
        _callbackAllocated = true;
        var desired = new AudioSpec
        {
            Freq = VoiceAudioBuffer.SampleRate,
            Channels = 1,
            Format = Sdl.AudioF32Sys,
            // 32 ms per callback: about thirty level updates a second for the meter.
            Samples = 512,
            Callback = _captureCallback,
        };
        AudioSpec obtained = default;
        _device = _audio.OpenAudioDevice((byte*)null, 1, in desired, ref obtained, 0);
        if (_device == 0)
            throw new VoiceInputException(VoiceInputErrorKind.Microphone, string.Format(Loc.Voice_MicrophoneOpenFailed, _audio.GetErrorS()));
        if (obtained.Freq != desired.Freq || obtained.Channels != desired.Channels || obtained.Format != desired.Format)
            throw new VoiceInputException(VoiceInputErrorKind.Microphone, Loc.Voice_MicrophoneFormatUnsupported);
    }

    private static string GetAudioLibraryPath()
    {
        var name = OperatingSystem.IsWindows() ? "SDL2.dll"
            : OperatingSystem.IsMacOS() ? "libSDL2-2.0.dylib" : "libSDL2-2.0.so";
        var architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        var rid = OperatingSystem.IsMacOS() ? "osx"
            : $"{(OperatingSystem.IsWindows() ? "win" : "linux")}-{architecture}";
        // RID publishes flatten native assets; framework-dependent builds keep runtimes/.
        // Single-file publishing extracts them alongside the managed binding assembly.
        foreach (var directory in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(typeof(Sdl).Assembly.Location) })
        {
            if (string.IsNullOrEmpty(directory))
                continue;
            foreach (var path in new[]
                     {
                         Path.Combine(directory, name),
                         Path.Combine(directory, "runtimes", rid, "native", name),
                     })
                if (File.Exists(path))
                    return path;
        }
        throw new FileNotFoundException(string.Format(Loc.Voice_MicrophoneLibraryMissing, name));
    }

    private unsafe void OnAudioProcessed(void* userData, byte* samples, int byteCount)
    {
        if (samples is not null && byteCount > 0)
        {
            _onAudio?.Invoke(new ReadOnlySpan<float>(samples, byteCount / sizeof(float)));
            _firstAudio.TrySetResult();
        }
    }

    public void StopCapture()
    {
        if (_audio is not null && _device != 0)
            _audio.PauseAudioDevice(_device, 1);
        _onAudio = null;
    }

    public async Task<VoiceTranscript> TranscribeAsync(
        float[] samples, string? language, bool preview, CancellationToken cancellationToken)
    {
        if (preview && samples.Length > VoiceInputService.PreviewMaxSamples)
            samples = samples[^VoiceInputService.PreviewMaxSamples..];
        // Only speech reaches Whisper, which would otherwise invent words for a fan, a click or a cough.
        if (_speechGate is not null && !_speechGate.DetectSpeech(samples).Any())
            return new VoiceTranscript("", null);
        // whisper.cpp needs at least a second of input, including for short words such as "yes".
        if (samples.Length < VoiceAudioBuffer.SampleRate)
            Array.Resize(ref samples, VoiceAudioBuffer.SampleRate);
        var processor = GetProcessor(preview, language, AudioContext(samples.Length));

        var text = new StringBuilder();
        string? heardLanguage = null;
        await foreach (var segment in processor.ProcessAsync(samples, cancellationToken))
        {
            heardLanguage ??= segment.Language;
            if (!IsNonSpeech(segment.Text))
                text.Append(segment.Text);
        }
        return new VoiceTranscript(text.ToString().Trim(), heardLanguage);
    }

    /// <summary>
    /// The encoder frames to look at: Whisper's whole window, or (for a model that keeps its accuracy that way) just the
    /// phrase and a margin, rounded up so that phrases of similar length share a processor.
    /// </summary>
    internal int AudioContext(int samples)
    {
        if (!_trimAudioWindow)
            return FullAudioContext;
        var frames = (samples + EncoderFrameSamples - 1) / EncoderFrameSamples + AudioContextMargin;
        return Math.Min(FullAudioContext, (frames + AudioContextMargin - 1) / AudioContextMargin * AudioContextMargin);
    }

    /// <summary>
    /// Whisper's notes about sound that isn't speech, such as "[BLANK_AUDIO]", "(music)" or "*sighs*", and text without a
    /// letter or digit, such as the "." it can hear in a room's noise.
    /// </summary>
    internal static bool IsNonSpeech(string text)
    {
        var trimmed = text.AsSpan().Trim();
        var hasWord = false;
        foreach (var character in trimmed.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(character))
            {
                hasWord = true;
                break;
            }
        }
        return !hasWord
            || trimmed.Length >= 2
            && (trimmed[0] == '[' && trimmed[^1] == ']'
                || trimmed[0] == '(' && trimmed[^1] == ')'
                || trimmed[0] == '*' && trimmed[^1] == '*'
                || trimmed[0] == '♪' && trimmed[^1] == '♪');
    }

    private WhisperProcessor GetProcessor(bool preview, string? language, int audioContext)
    {
        ref var slot = ref (preview ? ref _preview : ref _final);
        if (slot.Processor is not null && slot.Language == language && slot.AudioContext == audioContext)
            return slot.Processor;

        slot.Processor?.Dispose();
        slot = default;
        var builder = (_factory ?? throw new InvalidOperationException("The voice model has not been loaded."))
            .CreateBuilder()
            .WithThreads(DecoderThreads)
            .WithNoContext();
        builder = language is null ? builder.WithLanguageDetection() : builder.WithLanguage(language);
        if (audioContext < FullAudioContext)
            builder = builder.WithAudioContextSize(audioContext);
        slot = new ProcessorSlot(builder.Build(), language, audioContext);
        return slot.Processor!;
    }

    private record struct ProcessorSlot(WhisperProcessor? Processor, string? Language, int AudioContext);

    public async ValueTask DisposeAsync()
    {
        try
        {
            StopCapture();
        }
        finally
        {
            try
            {
                if (_device != 0)
                {
                    _audio?.CloseAudioDevice(_device);
                    _device = 0;
                }
            }
            finally
            {
                try
                {
                    if (_callbackAllocated)
                    {
                        _captureCallback.Dispose();
                        _callbackAllocated = false;
                    }
                    if (_audioInitialized)
                    {
                        _audio?.QuitSubSystem(Sdl.InitAudio);
                        _audioInitialized = false;
                    }
                    _audio?.Dispose();
                }
                finally
                {
                    try
                    {
                        if (_preview.Processor is not null)
                            await _preview.Processor.DisposeAsync();
                        if (_final.Processor is not null)
                            await _final.Processor.DisposeAsync();
                        _speechGate?.Dispose();
                        _speechGateFactory?.Dispose();
                    }
                    finally { _factory?.Dispose(); }
                }
            }
        }
    }
}
