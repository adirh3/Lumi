using System.Security.Cryptography;
using Lumi.Services;
using Whisper.net.Wave;
using Xunit;

namespace Lumi.Tests;

public sealed class VoiceInputNativeTests
{
    [SkippableFact]
    public async Task BundledDecoderRecognizesTheKnownSpeechFixtureInFinalAndPreviewDecodes()
    {
        var model = Environment.GetEnvironmentVariable("LUMI_TEST_VOICE_MODEL");
        var audio = Environment.GetEnvironmentVariable("LUMI_TEST_VOICE_AUDIO");
        Skip.If(string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(audio),
            "Set LUMI_TEST_VOICE_MODEL and LUMI_TEST_VOICE_AUDIO to a cached model and a 16 kHz 'quick brown fox' WAV fixture.");
        if (Environment.GetEnvironmentVariable("LUMI_TEST_VOICE_DOWNLOAD") == "1" && CatalogModel(model!) is { } pinned)
            await VoiceModelStore.For(pinned, Path.GetDirectoryName(Path.GetFullPath(model!))!).DownloadAsync(null, default);
        Assert.True(File.Exists(model), "The requested model fixture is missing.");
        Assert.True(File.Exists(audio), "The requested audio fixture is missing.");
        using var stream = File.OpenRead(audio!);
        var samples = await new WaveParser(stream).GetAvgSamplesAsync();
        await using var backend = new LocalVoiceInputBackend(Model(model!, trimAudioWindow: CatalogModel(model!)?.TrimAudioWindow ?? false));
        await backend.LoadModelAsync(default);

        var final = await backend.TranscribeAsync(samples, null, preview: false, default);
        Assert.Contains("quick brown fox", final.Text.ToLowerInvariant());
        Assert.Equal("en", final.Language);
        var preview = await backend.TranscribeAsync(samples, final.Language, preview: true, default);
        Assert.Contains("quick brown fox", preview.Text.ToLowerInvariant());
    }

    [SkippableFact]
    public void ACachedModelMatchesThePinnedDownload()
    {
        var model = Environment.GetEnvironmentVariable("LUMI_TEST_VOICE_MODEL");
        Skip.If(string.IsNullOrWhiteSpace(model) || !File.Exists(model) || CatalogModel(model) is null,
            "Set LUMI_TEST_VOICE_MODEL to a cached model Lumi downloads.");
        var pinned = CatalogModel(model!)!;
        using var file = File.OpenRead(model!);
        Assert.Equal(pinned.Size, file.Length);
        Assert.Equal(pinned.Sha256, Convert.ToHexStringLower(SHA256.HashData(file)));
    }

    [SkippableFact]
    public void TheGpuIsFoundOnlyWhereWhisperCanUseIt()
    {
        var expected = Environment.GetEnvironmentVariable("LUMI_TEST_VOICE_GPU");
        Skip.If(expected is not ("0" or "1"), "Set LUMI_TEST_VOICE_GPU to 1 or 0 for whether this computer has a GPU Whisper can use.");
        Assert.Equal(expected == "1", VoiceHardware.HasGpu());
    }

    [SkippableFact]
    public async Task AFileWhisperCannotReadIsReportedAsADamagedModel()
    {
        Skip.If(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LUMI_TEST_VOICE_MODEL")),
            "Set LUMI_TEST_VOICE_MODEL to run the native Whisper checks.");
        var path = Path.Combine(Path.GetTempPath(), $"Lumi-voice-damaged-{Guid.NewGuid():N}.bin");
        await File.WriteAllTextAsync(path, "not a whisper model");
        try
        {
            await using var backend = new LocalVoiceInputBackend(Model(path));
            var error = await Assert.ThrowsAsync<VoiceInputException>(() => backend.LoadModelAsync(default));
            Assert.Equal(VoiceInputErrorKind.Model, error.Kind);
        }
        finally { File.Delete(path); }
    }

    [SkippableFact]
    public async Task TheSpeechGateKeepsNoiseFromWhisperAndLetsSpeechThrough()
    {
        var model = Environment.GetEnvironmentVariable("LUMI_TEST_VOICE_MODEL");
        var gate = Environment.GetEnvironmentVariable("LUMI_TEST_VOICE_GATE");
        var audio = Environment.GetEnvironmentVariable("LUMI_TEST_VOICE_AUDIO");
        Skip.If(string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(gate) || string.IsNullOrWhiteSpace(audio),
            "Set LUMI_TEST_VOICE_MODEL, LUMI_TEST_VOICE_GATE and LUMI_TEST_VOICE_AUDIO to run the speech gate check.");
        using var stream = File.OpenRead(audio!);
        var speech = await new WaveParser(stream).GetAvgSamplesAsync();
        var random = new Random(1);
        var hiss = Enumerable.Range(0, VoiceAudioBuffer.SampleRate * 3)
            .Select(_ => (float)((random.NextDouble() * 2 - 1) * 0.05)).ToArray();
        await using var backend = new LocalVoiceInputBackend(Model(model!, gate));
        await backend.LoadModelAsync(default);

        Assert.Equal(new VoiceTranscript("", null), await backend.TranscribeAsync(hiss, null, preview: false, default));
        Assert.Equal("", (await backend.TranscribeAsync(hiss, "en", preview: true, default)).Text);
        Assert.Contains("quick brown fox", (await backend.TranscribeAsync(speech, null, preview: false, default)).Text.ToLowerInvariant());
    }

    [SkippableFact]
    public async Task ADamagedSpeechGateIsReportedAsADamagedModel()
    {
        var model = Environment.GetEnvironmentVariable("LUMI_TEST_VOICE_MODEL");
        Skip.If(string.IsNullOrWhiteSpace(model) || !File.Exists(model), "Set LUMI_TEST_VOICE_MODEL to run the native Whisper checks.");
        var path = Path.Combine(Path.GetTempPath(), $"Lumi-voice-damaged-gate-{Guid.NewGuid():N}.bin");
        await File.WriteAllTextAsync(path, "not a speech gate");
        try
        {
            await using var backend = new LocalVoiceInputBackend(Model(model!, path));
            var error = await Assert.ThrowsAsync<VoiceInputException>(() => backend.LoadModelAsync(default));
            Assert.Equal(VoiceInputErrorKind.Model, error.Kind);
        }
        finally { File.Delete(path); }
    }

    [SkippableFact]
    public async Task NativeMicrophoneDeliversSamplesAndCanBeReopened()
    {
        Skip.If(Environment.GetEnvironmentVariable("LUMI_TEST_VOICE_CAPTURE") != "1",
            "Set LUMI_TEST_VOICE_CAPTURE=1 for an explicit native microphone check.");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var backend = new LocalVoiceInputBackend(Model(""));
            var ready = VoiceInputServiceTests.Signal();
            var count = 0;
            await backend.StartCaptureAsync(samples =>
            {
                if (Interlocked.Add(ref count, samples.Length) >= VoiceAudioBuffer.SampleRate)
                    ready.TrySetResult();
            }, CancellationToken.None);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            backend.StopCapture();
            Assert.True(count >= VoiceAudioBuffer.SampleRate);
        }
    }

    [Theory]
    [InlineData(" [BLANK_AUDIO]", true)]
    [InlineData("(upbeat music)", true)]
    [InlineData(" *sighs* ", true)]
    [InlineData("♪ la la ♪", true)]
    [InlineData("  ", true)]
    [InlineData(" .", true)]
    [InlineData("...", true)]
    [InlineData(" - ?!", true)]
    [InlineData(" Send it (please) now.", false)]
    [InlineData(" Hello there.", false)]
    [InlineData(" 42.", false)]
    [InlineData(" שלום.", false)]
    [InlineData(" 你好。", false)]
    public void WhisperNotesAboutNonSpeechAreDropped(string segment, bool dropped)
        => Assert.Equal(dropped, LocalVoiceInputBackend.IsNonSpeech(segment));

    private static VoiceModel? CatalogModel(string path)
        => new[] { VoiceModel.Turbo, VoiceModel.Small }.FirstOrDefault(model => model.FileName == Path.GetFileName(path));

    private static VoiceModelStore Model(string path, string? speechGate = null, bool trimAudioWindow = false)
        => new(path, _ => throw new InvalidOperationException("Native tests never download."), 0, sha256: null, trimAudioWindow,
            speechGate is null ? null
                : new VoiceModelFile(speechGate, _ => throw new InvalidOperationException("Native tests never download."), 0, null));
}
