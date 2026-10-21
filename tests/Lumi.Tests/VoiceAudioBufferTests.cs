using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class VoiceAudioBufferTests
{
    private const int Chunk = 1600;
    private const int PauseChunks = VoiceAudioBuffer.PauseSamples / Chunk;

    [Fact]
    public void SilenceNeverBecomesADictationSegment()
    {
        var buffer = new VoiceAudioBuffer();
        for (var i = 0; i < 100; i++)
            Assert.Null(buffer.Append(new float[Chunk]));
        Assert.Null(buffer.Drain());
    }

    [Fact]
    public void APauseEndsThePhraseWithABoundedPreRollAndDoesNotRepeatIt()
    {
        var buffer = new VoiceAudioBuffer();
        for (var i = 0; i < 10; i++)
            Assert.Null(buffer.Append(Room(Chunk)));
        Assert.Null(buffer.Append(Speech(3200)));

        for (var i = 0; i < PauseChunks - 1; i++)
            Assert.Null(buffer.Append(Room(Chunk)));
        var phrase = buffer.Append(Room(Chunk));

        Assert.NotNull(phrase);
        Assert.Equal(4000 + 3200 + VoiceAudioBuffer.PauseSamples, phrase.Length);
        Assert.Equal(0.1f, phrase[4000]);
        Assert.Null(buffer.Drain());
        Assert.Null(buffer.Append(Room(16000)));
    }

    [Fact]
    public void StoppingKeepsTheFinalShortPhrase()
    {
        var buffer = new VoiceAudioBuffer();
        Assert.Null(buffer.Append(Said(3200)));
        Assert.Equal(Said(3200), buffer.Drain());
        Assert.Null(buffer.Drain());
    }

    [Fact]
    public void AClickTooShortToHoldAWordIsIgnored()
    {
        var buffer = new VoiceAudioBuffer();
        for (var i = 0; i < 10; i++)
            buffer.Append(Noise(Chunk, 0.0002f));
        buffer.Append(Speech(Chunk));
        Assert.Null(buffer.Snapshot(1, 100_000));
        for (var i = 0; i < PauseChunks + 2; i++)
            Assert.Null(buffer.Append(Noise(Chunk, 0.0002f)));
        Assert.Null(buffer.Drain());
    }

    [Fact]
    public void AQuietMicrophoneIsStillHeard()
    {
        // Speech peaking near -56 dBFS in a quiet room, as from a laptop microphone set low.
        var buffer = new VoiceAudioBuffer();
        for (var i = 0; i < 10; i++)
            buffer.Append(Noise(Chunk, 0.0002f));
        buffer.Append(Noise(3200, 0.0016f));
        float[]? phrase = null;
        for (var i = 0; i <= PauseChunks && phrase is null; i++)
            phrase = buffer.Append(Noise(Chunk, 0.0002f));
        Assert.NotNull(phrase);
    }

    [Fact]
    public void SpeechBarelyAboveALoudHumIsStillHeard()
    {
        // A hum at -32 dBFS with speech only 8 dB above it, as some headsets and laptops record.
        var buffer = new VoiceAudioBuffer();
        for (var i = 0; i < 10; i++)
            buffer.Append(Noise(Chunk, 0.025f));
        buffer.Append(Noise(3200, 0.06f));
        float[]? phrase = null;
        for (var i = 0; i <= PauseChunks && phrase is null; i++)
            phrase = buffer.Append(Noise(Chunk, 0.025f));
        Assert.NotNull(phrase);
    }

    [Fact]
    public void ContinuousSpeechIsSplitInsteadOfGrowingWithoutBound()
    {
        var buffer = new VoiceAudioBuffer();
        float[]? phrase = null;
        // Speech with the short dips between words, never a pause.
        for (var i = 0; i < 300; i++)
            phrase = buffer.Append(i % 2 == 0 ? Room(Chunk) : Speech(Chunk));
        Assert.NotNull(phrase);
        Assert.Equal(VoiceAudioBuffer.SampleRate * 30, phrase.Length);
        Assert.Null(buffer.Drain());
        Assert.Null(buffer.Append(Speech(3200)));
        Assert.Equal(3200, buffer.Drain()!.Length);
    }

    [Fact]
    public void SteadyBackgroundNoiseIsTheRoomNotAPhrase()
    {
        var buffer = new VoiceAudioBuffer();
        // A fan from the moment the microphone opens, after the digital silence some microphones start with, well
        // above the minimum speech level.
        for (var i = 0; i < 5; i++)
            Assert.Null(buffer.Append(new float[Chunk]));
        for (var i = 0; i < 60; i++)
            Assert.Null(buffer.Append(Noise(Chunk, 0.01f)));
        Assert.Null(buffer.Snapshot(1, 100_000));
        Assert.Null(buffer.Drain());

        Assert.Null(buffer.Append(Speech(3200)));
        float[]? phrase = null;
        for (var i = 0; i <= PauseChunks && phrase is null; i++)
            phrase = buffer.Append(Noise(Chunk, 0.01f));
        Assert.NotNull(phrase);
    }

    [Fact]
    public void ASnapshotShowsTheNewestAudioOfAPhraseOnceItHoldsAWord()
    {
        var buffer = new VoiceAudioBuffer();
        buffer.Append(Room(Chunk));
        Assert.Null(buffer.Snapshot(1, 100));
        buffer.Append(Speech(Chunk));
        Assert.Null(buffer.Snapshot(1, 100_000));

        buffer.Append(Speech(3200));
        Assert.Null(buffer.Snapshot(10_000, 100_000));
        var snapshot = buffer.Snapshot(3200, 1000);
        Assert.NotNull(snapshot);
        Assert.Equal(Chunk + Chunk + 3200, snapshot.Length);
        Assert.Equal(1000, snapshot.Samples.Length);
        Assert.True(buffer.IsCurrent(snapshot.Phrase));

        buffer.Drain();
        Assert.False(buffer.IsCurrent(snapshot.Phrase));
    }

    [Fact]
    public void OnlyDigitalSilenceCountsAsHearingNothing()
    {
        var buffer = new VoiceAudioBuffer();
        buffer.Append(new float[16000]);
        Assert.False(buffer.HeardSignal);
        buffer.Append(Noise(Chunk, 0.0005f));
        Assert.True(buffer.HeardSignal);
    }

    [Theory]
    [InlineData(0, 0.001, 0)]
    [InlineData(0.001, 0.001, 0)]
    [InlineData(0.01, 0.001, 0.35)]
    [InlineData(0.1, 0.001, 0.85)]
    [InlineData(1, 0.001, 1)]
    // A quiet microphone moves the meter as much as a loud one.
    [InlineData(0.001, 0.0001, 0.35)]
    public void LoudnessMapsToAMeterLevelAboveTheRoom(double rms, double floor, double level)
        => Assert.Equal(level, VoiceAudioBuffer.ToLevel(rms, floor), precision: 6);

    [Fact]
    public void TheMeterRisesWithSpeechAboveTheRoom()
    {
        var buffer = new VoiceAudioBuffer();
        buffer.Append(new float[Chunk], out var silence);
        for (var i = 0; i < 10; i++)
            buffer.Append(Room(Chunk), out _);
        buffer.Append(Room(Chunk), out var room);
        buffer.Append(Speech(Chunk), out var speech);
        Assert.Equal(0, silence);
        Assert.Equal(0, room);
        Assert.True(speech > 0.5, $"Speech showed as {speech}.");
    }

    /// <summary>A quiet room, about -60 dBFS.</summary>
    internal static float[] Room(int length) => Noise(length, 0.001f);

    /// <summary>Speech, about -20 dBFS.</summary>
    internal static float[] Speech(int length)
        => Enumerable.Repeat(0.1f, length).ToArray();

    /// <summary>Something said after a moment of the room, which is how a microphone first hears a speaker.</summary>
    internal static float[] Said(int length) => [.. Room(Chunk), .. Speech(length)];

    /// <summary>The room for long enough to end a phrase.</summary>
    internal static float[] Pause() => Room(VoiceAudioBuffer.PauseSamples + Chunk);

    private static float[] Noise(int length, float level)
        => Enumerable.Repeat(level, length).ToArray();
}

internal static class VoiceAudioBufferTestExtensions
{
    /// <summary>Appends audio for a test that doesn't look at the meter.</summary>
    public static float[]? Append(this VoiceAudioBuffer buffer, ReadOnlySpan<float> samples) => buffer.Append(samples, out _);
}
