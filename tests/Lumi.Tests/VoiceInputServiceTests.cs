using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class VoiceInputServiceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData("WINDOWS", Architecture.X64, true)]
    [InlineData("WINDOWS", Architecture.Arm64, true)]
    [InlineData("LINUX", Architecture.X64, true)]
    [InlineData("LINUX", Architecture.Arm64, true)]
    [InlineData("OSX", Architecture.X64, true)]
    [InlineData("OSX", Architecture.Arm64, true)]
    [InlineData("FREEBSD", Architecture.X64, false)]
    [InlineData("WINDOWS", Architecture.X86, false)]
    public void AvailabilityMatchesBundledDesktopRuntimes(string platform, Architecture architecture, bool expected)
        => Assert.Equal(expected, VoiceInputService.IsSupportedPlatform(OSPlatform.Create(platform), architecture));

    [Fact]
    public async Task PhrasesLandAtPausesAndTheLastOneWhenFinishing()
    {
        var backend = new FakeVoiceInputBackend { Results = ["first phrase", "last phrase"] };
        using var service = new VoiceInputService(() => backend, TestVoiceModels.Installed());
        var results = new ConcurrentQueue<string>();
        var firstResult = Signal();
        service.ResultGenerated += text => { results.Enqueue(text); firstResult.TrySetResult(); };
        var errors = new ConcurrentQueue<VoiceInputError>();
        service.Error += errors.Enqueue;

        await service.StartAsync().WaitAsync(Timeout);
        Assert.Equal(VoiceInputState.Listening, service.State);
        backend.Emit(VoiceAudioBufferTests.Said(3200));
        backend.Emit(VoiceAudioBufferTests.Pause());
        await firstResult.Task.WaitAsync(Timeout);
        Assert.Equal(VoiceInputState.Listening, service.State);
        backend.Emit(VoiceAudioBufferTests.Speech(3200));
        await service.StopAsync().WaitAsync(Timeout);

        Assert.Equal(["first phrase", "last phrase"], results.ToArray());
        Assert.Empty(errors);
        Assert.Equal(VoiceInputState.Idle, service.State);
        Assert.True(backend.Disposed);
        Assert.False(backend.IsCapturing);
    }

    [Fact]
    public async Task TheMicrophoneOpensBeforeTheModelLoadsAndKeepsWhatWasSaidMeanwhile()
    {
        var backend = new FakeVoiceInputBackend { ModelGate = Signal() };
        using var service = new VoiceInputService(() => backend, TestVoiceModels.Installed());
        var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.ResultGenerated += text => result.TrySetResult(text);

        await service.StartAsync().WaitAsync(Timeout);
        Assert.Equal(VoiceInputState.Listening, service.State);
        Assert.False(backend.ModelGate!.Task.IsCompleted);
        backend.Emit(VoiceAudioBufferTests.Said(3200));
        backend.Emit(VoiceAudioBufferTests.Pause());
        await Task.Delay(100);
        Assert.Equal(0, backend.TranscriptionCount);

        backend.ModelGate.TrySetResult();
        Assert.Equal("voice phrase", await result.Task.WaitAsync(Timeout));
        await service.StopAsync().WaitAsync(Timeout);
    }

    [Fact]
    public async Task WordsArePreviewedWhileSpeakingAndClearedOnceThePhraseLands()
    {
        var backend = new FakeVoiceInputBackend { Preview = "voice phr" };
        using var service = new VoiceInputService(() => backend, TestVoiceModels.Installed());
        var previews = new ConcurrentQueue<string>();
        var previewed = Signal();
        var cleared = Signal();
        service.PreviewChanged += text =>
        {
            previews.Enqueue(text);
            (text.Length > 0 ? previewed : cleared).TrySetResult();
        };
        var results = new ConcurrentQueue<string>();
        service.ResultGenerated += results.Enqueue;

        await service.StartAsync().WaitAsync(Timeout);
        backend.Emit(VoiceAudioBufferTests.Said(16000));
        await previewed.Task.WaitAsync(Timeout);
        Assert.Empty(results);

        backend.Emit(VoiceAudioBufferTests.Pause());
        await cleared.Task.WaitAsync(Timeout);
        Assert.Equal(["voice phrase"], results.ToArray());
        Assert.Equal(["voice phr", ""], previews.ToArray());
        await service.StopAsync().WaitAsync(Timeout);
    }

    [Fact]
    public async Task ALongFirstPhraseKeepsItsLanguageForTheRestOfTheDictation()
    {
        var backend = new FakeVoiceInputBackend { Results = ["שלום לכולם", "ועוד משפט"], DetectedLanguage = "he" };
        using var service = new VoiceInputService(() => backend, TestVoiceModels.Installed());
        var results = new ConcurrentQueue<string>();
        var first = Signal();
        service.ResultGenerated += text => { results.Enqueue(text); first.TrySetResult(); };

        await service.StartAsync().WaitAsync(Timeout);
        backend.Emit(VoiceAudioBufferTests.Said(32000));
        backend.Emit(VoiceAudioBufferTests.Pause());
        await first.Task.WaitAsync(Timeout);
        backend.Emit(VoiceAudioBufferTests.Speech(16000));
        await service.StopAsync().WaitAsync(Timeout);

        Assert.Equal([null, "he"], backend.FinalLanguages.ToArray());
        Assert.Equal(["שלום לכולם", "ועוד משפט"], results.ToArray());
    }

    [Fact]
    public async Task AShortFirstPhraseKeepsDetectingTheLanguage()
    {
        var backend = new FakeVoiceInputBackend { Results = ["ok", "and more"], DetectedLanguage = "en" };
        using var service = new VoiceInputService(() => backend, TestVoiceModels.Installed());
        var first = Signal();
        service.ResultGenerated += _ => first.TrySetResult();

        await service.StartAsync().WaitAsync(Timeout);
        backend.Emit(VoiceAudioBufferTests.Said(3200));
        backend.Emit(VoiceAudioBufferTests.Pause());
        await first.Task.WaitAsync(Timeout);
        backend.Emit(VoiceAudioBufferTests.Speech(3200));
        await service.StopAsync().WaitAsync(Timeout);

        Assert.Equal([null, null], backend.FinalLanguages.ToArray());
    }

    [Fact]
    public async Task AChosenLanguageIsHeardFromTheFirstWordsAndCanChangeMidDictation()
    {
        // Detection would mishear these words as Arabic.
        var backend = new FakeVoiceInputBackend { Results = ["שלום לכולם", "hello", "again"], DetectedLanguage = "ar" };
        using var service = new VoiceInputService(() => backend, TestVoiceModels.Installed());
        var landed = new SemaphoreSlim(0);
        service.ResultGenerated += _ => landed.Release();

        await service.StartAsync(" HE ").WaitAsync(Timeout);
        backend.Emit(VoiceAudioBufferTests.Said(32000));
        backend.Emit(VoiceAudioBufferTests.Pause());
        Assert.True(await landed.WaitAsync(Timeout));
        service.SetLanguage("en");
        backend.Emit(VoiceAudioBufferTests.Speech(3200));
        backend.Emit(VoiceAudioBufferTests.Pause());
        Assert.True(await landed.WaitAsync(Timeout));
        service.SetLanguage("");
        backend.Emit(VoiceAudioBufferTests.Speech(3200));
        await service.StopAsync().WaitAsync(Timeout);

        Assert.Equal(["he", "en", null], backend.FinalLanguages.ToArray());
    }

    [Fact]
    public async Task ChoosingAutoDetectAgainDetectsTheLanguageAfresh()
    {
        var backend = new FakeVoiceInputBackend { Results = ["hello there", "שלום", "again"], DetectedLanguage = "en" };
        using var service = new VoiceInputService(() => backend, TestVoiceModels.Installed());
        var landed = new SemaphoreSlim(0);
        service.ResultGenerated += _ => landed.Release();

        // A long first phrase sets the dictation's language...
        await service.StartAsync().WaitAsync(Timeout);
        backend.Emit(VoiceAudioBufferTests.Said(32000));
        backend.Emit(VoiceAudioBufferTests.Pause());
        Assert.True(await landed.WaitAsync(Timeout));
        service.SetLanguage("he");
        backend.Emit(VoiceAudioBufferTests.Speech(3200));
        backend.Emit(VoiceAudioBufferTests.Pause());
        Assert.True(await landed.WaitAsync(Timeout));
        // ...until the user chooses one: Auto-detect then listens afresh instead of for the language found before.
        service.SetLanguage(null);
        backend.Emit(VoiceAudioBufferTests.Speech(3200));
        await service.StopAsync().WaitAsync(Timeout);

        Assert.Equal([null, "he", null], backend.FinalLanguages.ToArray());
    }

    [Fact]
    public async Task AutoDetectChosenWhileAPhraseFinishesKeepsNoLanguage()
    {
        var backend = new FakeVoiceInputBackend { Results = ["שלום לכולם", "hello"], TranscribeGate = Signal() };
        using var service = new VoiceInputService(() => backend, TestVoiceModels.Installed());
        var landed = Signal();
        service.ResultGenerated += _ => landed.TrySetResult();

        await service.StartAsync("he").WaitAsync(Timeout);
        backend.Emit(VoiceAudioBufferTests.Said(32000));
        backend.Emit(VoiceAudioBufferTests.Pause());
        // The phrase is heard in Hebrew because Hebrew was chosen; switching to Auto-detect meanwhile detects next time.
        await backend.Transcribing.Task.WaitAsync(Timeout);
        service.SetLanguage("");
        backend.TranscribeGate!.TrySetResult();
        await landed.Task.WaitAsync(Timeout);
        backend.Emit(VoiceAudioBufferTests.Speech(3200));
        await service.StopAsync().WaitAsync(Timeout);

        Assert.Equal(["he", null], backend.FinalLanguages.ToArray());
    }

    [Fact]
    public async Task TheChosenSpeechModelIsTheOneThatListens()
    {
        var model = TestVoiceModels.Installed();
        VoiceModelStore? listening = null;
        var backend = new FakeVoiceInputBackend();
        using var service = new VoiceInputService(chosen => { listening = chosen; return backend; }, () => Task.FromResult(model));

        Assert.Same(model, await service.GetModelAsync());
        await service.StartAsync().WaitAsync(Timeout);
        await service.CancelAsync().WaitAsync(Timeout);
        Assert.Same(model, listening);
    }

    [Fact]
    public async Task MicrophoneLevelsAreReportedWhileListening()
    {
        var backend = new FakeVoiceInputBackend();
        using var service = new VoiceInputService(() => backend, TestVoiceModels.Installed());
        var levels = new ConcurrentQueue<double>();
        service.LevelChanged += levels.Enqueue;

        await service.StartAsync().WaitAsync(Timeout);
        backend.Emit(VoiceAudioBufferTests.Room(1600));
        backend.Emit(VoiceAudioBufferTests.Speech(512));
        await service.CancelAsync().WaitAsync(Timeout);

        Assert.Equal(2, levels.Count);
        Assert.Equal(0, levels.First());
        Assert.InRange(levels.Last(), 0.5, 1);
    }

    [Fact]
    public async Task WaitingForAudioIsCancellableAndDoesNotClaimToBeListening()
    {
        var backend = new FakeVoiceInputBackend { CaptureGate = Signal() };
        using var service = new VoiceInputService(() => backend, TestVoiceModels.Installed());
        var errors = new ConcurrentQueue<VoiceInputError>();
        service.Error += errors.Enqueue;
        var starting = service.StartAsync();
        await backend.CaptureStarting.Task.WaitAsync(Timeout);
        Assert.Equal(VoiceInputState.Starting, service.State);
        await service.CancelAsync().WaitAsync(Timeout);
        await starting;
        Assert.Empty(errors);
        Assert.False(backend.IsCapturing);
        Assert.True(backend.Disposed);
    }

    [Fact]
    public async Task FinishingBeforeAnyAudioJustStops()
    {
        var backend = new FakeVoiceInputBackend { CaptureGate = Signal() };
        using var service = new VoiceInputService(() => backend, TestVoiceModels.Installed());
        var errors = new ConcurrentQueue<VoiceInputError>();
        service.Error += errors.Enqueue;
        _ = service.StartAsync();
        await backend.CaptureStarting.Task.WaitAsync(Timeout);
        await service.StopAsync().WaitAsync(Timeout);
        Assert.Empty(errors);
        Assert.Equal(VoiceInputState.Idle, service.State);
        Assert.Equal(0, backend.TranscriptionCount);
    }

    [Fact]
    public async Task AMicrophoneThatSendsNoAudioIsReportedAsAMicrophoneProblem()
    {
        var backend = new FakeVoiceInputBackend
        {
            CaptureError = new VoiceInputException(VoiceInputErrorKind.Microphone, "no audio received")
        };
        using var service = new VoiceInputService(() => backend, TestVoiceModels.Installed());
        var errors = new ConcurrentQueue<VoiceInputError>();
        service.Error += errors.Enqueue;
        await service.StartAsync().WaitAsync(Timeout);
        Assert.Equal([new VoiceInputError(VoiceInputErrorKind.Microphone, "no audio received")], errors.ToArray());
        Assert.False(backend.IsCapturing);
        Assert.True(backend.Disposed);
    }

    [Fact]
    public async Task UnexpectedFailuresAreReportedAndANewAttemptCanSucceed()
    {
        var failed = new FakeVoiceInputBackend { CaptureError = new InvalidOperationException("device lost") };
        var healthy = new FakeVoiceInputBackend();
        var attempts = 0;
        using var service = new VoiceInputService(() => ++attempts == 1 ? failed : healthy, TestVoiceModels.Installed());
        var errors = new ConcurrentQueue<VoiceInputError>();
        service.Error += errors.Enqueue;

        await service.StartAsync().WaitAsync(Timeout);
        Assert.Equal([new VoiceInputError(VoiceInputErrorKind.Failed, "device lost")], errors.ToArray());
        Assert.True(failed.Disposed);
        Assert.Equal(VoiceInputState.Idle, service.State);
        await service.StartAsync().WaitAsync(Timeout);
        healthy.Emit(VoiceAudioBufferTests.Said(3200));
        await service.StopAsync().WaitAsync(Timeout);

        Assert.Equal(2, attempts);
        Assert.Single(errors);
        Assert.True(healthy.Disposed);
    }

    [Fact]
    public async Task ADamagedModelIsRemovedSoVoiceTypingCanBeSetUpAgain()
    {
        var model = TestVoiceModels.Installed();
        var backend = new FakeVoiceInputBackend
        {
            ModelError = new VoiceInputException(VoiceInputErrorKind.Model, "damaged")
        };
        using var service = new VoiceInputService(() => backend, model);
        var errors = new ConcurrentQueue<VoiceInputError>();
        var reported = Signal();
        service.Error += error => { errors.Enqueue(error); reported.TrySetResult(); };

        await service.StartAsync().WaitAsync(Timeout);
        await reported.Task.WaitAsync(Timeout);
        Assert.Equal(VoiceInputErrorKind.Model, Assert.Single(errors).Kind);
        Assert.False(model.IsInstalled);
        Assert.False((await service.GetModelAsync()).IsInstalled);
    }

    [Fact]
    public async Task TranscriptionFailureIsVisibleAndClosesTheMicrophone()
    {
        var backend = new FakeVoiceInputBackend { TranscribeError = new InvalidOperationException("decoder failed") };
        using var service = new VoiceInputService(() => backend, TestVoiceModels.Installed());
        var stopped = Signal();
        var errors = new ConcurrentQueue<VoiceInputError>();
        service.Error += errors.Enqueue;
        service.StateChanged += state => { if (state == VoiceInputState.Idle) stopped.TrySetResult(); };
        await service.StartAsync().WaitAsync(Timeout);
        backend.Emit(VoiceAudioBufferTests.Said(3200));
        backend.Emit(VoiceAudioBufferTests.Pause());
        await stopped.Task.WaitAsync(Timeout);

        Assert.Equal([new VoiceInputError(VoiceInputErrorKind.Failed, "decoder failed")], errors.ToArray());
        Assert.False(backend.IsCapturing);
        Assert.True(backend.Disposed);
    }

    [Fact]
    public async Task CancellingTranscriptionRejectsEvenALateDecoderResult()
    {
        var backend = new FakeVoiceInputBackend { TranscribeGate = Signal(), IgnoreTranscriptionCancellation = true };
        using var service = new VoiceInputService(() => backend, TestVoiceModels.Installed());
        var results = new ConcurrentQueue<string>();
        var errors = new ConcurrentQueue<VoiceInputError>();
        service.ResultGenerated += results.Enqueue;
        service.Error += errors.Enqueue;
        await service.StartAsync().WaitAsync(Timeout);
        backend.Emit(VoiceAudioBufferTests.Said(3200));
        backend.Emit(VoiceAudioBufferTests.Pause());
        await backend.Transcribing.Task.WaitAsync(Timeout);
        var cancellation = service.CancelAsync();
        await backend.CaptureStopped.Task.WaitAsync(Timeout);
        backend.TranscribeGate!.TrySetResult();
        await cancellation.WaitAsync(Timeout);

        Assert.Empty(results);
        Assert.Empty(errors);
        Assert.True(backend.Disposed);
    }

    [Fact]
    public async Task DigitalSilenceIsReportedAsASilentMicrophone()
    {
        var backend = new FakeVoiceInputBackend();
        using var service = new VoiceInputService(() => backend, TestVoiceModels.Installed());
        var errors = new ConcurrentQueue<VoiceInputError>();
        service.Error += errors.Enqueue;
        await service.StartAsync().WaitAsync(Timeout);
        backend.Emit(new float[16000]);
        await service.StopAsync().WaitAsync(Timeout);
        Assert.Equal([new VoiceInputError(VoiceInputErrorKind.Silent)], errors.ToArray());
        Assert.Equal(0, backend.TranscriptionCount);
    }

    [Fact]
    public async Task AQuietRoomWithoutWordsIsReportedAsNoSpeech()
    {
        var backend = new FakeVoiceInputBackend();
        using var service = new VoiceInputService(() => backend, TestVoiceModels.Installed());
        var errors = new ConcurrentQueue<VoiceInputError>();
        service.Error += errors.Enqueue;
        await service.StartAsync().WaitAsync(Timeout);
        backend.Emit(Enumerable.Repeat(0.0005f, 16000).ToArray());
        await service.StopAsync().WaitAsync(Timeout);
        Assert.Equal([new VoiceInputError(VoiceInputErrorKind.NoSpeech)], errors.ToArray());
    }

    [Fact]
    public async Task DisposingCancelsWithoutBlockingOrPublishingMoreEvents()
    {
        var backend = new FakeVoiceInputBackend();
        var service = new VoiceInputService(() => backend, TestVoiceModels.Installed());
        await service.StartAsync().WaitAsync(Timeout);
        var notifications = 0;
        service.StateChanged += _ => Interlocked.Increment(ref notifications);
        service.Dispose();
        await service.CancelAsync().WaitAsync(Timeout);
        Assert.Equal(0, notifications);
        Assert.True(backend.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.StartAsync());
    }

    internal static TaskCompletionSource Signal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>Speech models for tests: tiny files in a temporary folder removed when the run ends, never the user's profile.</summary>
internal static class TestVoiceModels
{
    private static readonly string Root = CreateRoot();

    public static VoiceModelStore Installed() => Create(installed: true);

    public static VoiceModelStore Missing(Func<CancellationToken, Task<Stream>>? download = null)
        => Create(installed: false, download);

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"Lumi-voice-tests-{Environment.ProcessId}");
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        };
        return root;
    }

    private static VoiceModelStore Create(bool installed, Func<CancellationToken, Task<Stream>>? download = null)
    {
        var path = Path.Combine(Root, Guid.NewGuid().ToString("N"), "model.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (installed)
            File.WriteAllBytes(path, [1, 2, 3, 4]);
        return new VoiceModelStore(
            path,
            download ?? (_ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3, 4]))),
            downloadSize: 4,
            sha256: null);
    }
}

internal sealed class FakeVoiceInputBackend : IVoiceInputBackend
{
    private VoiceAudioCallback? _onAudio;
    private int _transcriptionCount;
    public TaskCompletionSource Transcribing { get; } = VoiceInputServiceTests.Signal();
    public TaskCompletionSource CaptureStopped { get; } = VoiceInputServiceTests.Signal();
    public TaskCompletionSource CaptureStarting { get; } = VoiceInputServiceTests.Signal();
    public TaskCompletionSource? ModelGate { get; init; }
    public TaskCompletionSource? TranscribeGate { get; init; }
    public TaskCompletionSource? CaptureGate { get; init; }
    public bool IgnoreTranscriptionCancellation { get; init; }
    public Exception? ModelError { get; init; }
    public Exception? TranscribeError { get; init; }
    public Exception? CaptureError { get; init; }
    public string[] Results { get; init; } = ["voice phrase"];
    /// <summary>What a live preview hears; empty means previews find no words.</summary>
    public string Preview { get; init; } = "";
    public string? DetectedLanguage { get; init; } = "en";
    public ConcurrentQueue<string?> FinalLanguages { get; } = new();
    public int StartCount { get; private set; }
    public int TranscriptionCount => Volatile.Read(ref _transcriptionCount);
    public bool IsCapturing => _onAudio is not null;
    public bool Disposed { get; private set; }

    public async Task LoadModelAsync(CancellationToken cancellationToken)
    {
        if (ModelGate is not null)
            await ModelGate.Task.WaitAsync(cancellationToken);
        if (ModelError is not null)
            throw ModelError;
    }

    public async Task StartCaptureAsync(VoiceAudioCallback onAudio, CancellationToken cancellationToken)
    {
        StartCount++;
        _onAudio = onAudio;
        CaptureStarting.TrySetResult();
        if (CaptureGate is not null)
            await CaptureGate.Task.WaitAsync(cancellationToken);
        if (CaptureError is not null)
            throw CaptureError;
    }

    public void Emit(float[] samples) => _onAudio?.Invoke(samples);

    public void StopCapture()
    {
        _onAudio = null;
        CaptureStopped.TrySetResult();
    }

    public async Task<VoiceTranscript> TranscribeAsync(
        float[] samples, string? language, bool preview, CancellationToken cancellationToken)
    {
        if (preview)
            return new VoiceTranscript(Preview, language ?? DetectedLanguage);

        var index = Interlocked.Increment(ref _transcriptionCount) - 1;
        FinalLanguages.Enqueue(language);
        Transcribing.TrySetResult();
        if (TranscribeGate is not null)
        {
            if (IgnoreTranscriptionCancellation)
                await TranscribeGate.Task;
            else
                await TranscribeGate.Task.WaitAsync(cancellationToken);
        }
        if (TranscribeError is not null)
            throw TranscribeError;
        return new VoiceTranscript(Results[Math.Min(index, Results.Length - 1)], language ?? DetectedLanguage);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
