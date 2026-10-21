namespace Lumi.Services;

/// <summary>
/// Splits microphone audio into phrases at natural pauses, keeping a short pre-roll and bounding each phrase to
/// 30 seconds. Loudness is judged in 20 ms frames against the room rather than an absolute level, since microphones
/// differ by 40 dB: speech is twice as loud as the quietest moment of the last five seconds (and above -68 dBFS), so a
/// quiet microphone is still heard, speech barely above a loud hum still counts, and a fan or a busy room still lets a
/// phrase end at a pause. The first sound only tells it how loud the room is. Nothing heard as speech is ever dropped;
/// only a phrase with too little speech to hold a word (a click or a bump) is ignored. The audio thread appends while
/// the decoder reads snapshots, so every member is thread-safe.
/// </summary>
internal sealed class VoiceAudioBuffer
{
    internal const int SampleRate = 16000;
    // A pause this long ends a phrase: Whisper is more accurate on whole sentences than on fragments, and the live
    // preview shows the words meanwhile.
    internal const int PauseSamples = SampleRate * 6 / 5;
    private const int FrameSamples = SampleRate / 50;
    private const int PreRollSamples = SampleRate / 4;
    private const int MaxSamples = SampleRate * 30;
    private const int MinSpeechSamples = SampleRate * 3 / 20;
    private const double MinimumSpeechRms = 0.0004;
    private const double NoiseMargin = 2;
    private const int NoiseWindowFrames = 250;
    // Quieter than any live microphone: digital silence from a muted or blocked input.
    private const double SignalRms = 0.00001;
    // The meter shows speech from 6 dB above the room's quietest moment up to 40 dB louder.
    private const double MeterHeadroomDb = 6;
    private const double MeterRangeDb = 40;

    private readonly object _gate = new();
    private readonly List<float> _samples = [];
    // The frames of the last five seconds; the room is as quiet as the quietest of them.
    private readonly double[] _recentRms = new double[NoiseWindowFrames];
    private int _recentNext;
    private double _frameEnergy;
    private int _frameFill;
    private bool _hasSpeech;
    private int _speechSamples;
    private int _silentSamples;
    private int _phrase;
    private bool _heardSignal;

    public VoiceAudioBuffer() => Array.Fill(_recentRms, double.PositiveInfinity);

    /// <summary>Whether anything other than digital silence has arrived.</summary>
    public bool HeardSignal
    {
        get { lock (_gate) return _heardSignal; }
    }

    /// <summary>Adds audio and returns a finished phrase when a pause or the length limit ends one.</summary>
    /// <param name="samples">Mono samples at <see cref="SampleRate"/>.</param>
    /// <param name="level">How loud <paramref name="samples"/> are for a level meter, from 0 (the room) to 1 (loud).</param>
    public float[]? Append(ReadOnlySpan<float> samples, out double level)
    {
        level = 0;
        if (samples.IsEmpty)
            return null;

        double energy = 0;
        foreach (var sample in samples)
            energy += sample * sample;
        var rms = Math.Sqrt(energy / samples.Length);

        lock (_gate)
        {
            _heardSignal |= rms > SignalRms;
            foreach (var sample in samples)
            {
                _samples.Add(sample);
                _frameEnergy += sample * sample;
                if (++_frameFill == FrameSamples)
                    EndFrame();
            }
            level = ToLevel(rms, Floor());

            if (!_hasSpeech)
            {
                if (_samples.Count > PreRollSamples)
                    _samples.RemoveRange(0, _samples.Count - PreRollSamples);
                return null;
            }

            return _silentSamples >= PauseSamples || _samples.Count >= MaxSamples ? DrainLocked() : null;
        }
    }

    /// <summary>
    /// A copy of the phrase in progress, at most <paramref name="maxSamples"/> of its newest audio, once it holds at
    /// least <paramref name="minSamples"/> and enough speech for a word; null otherwise.
    /// </summary>
    public VoicePhraseSnapshot? Snapshot(int minSamples, int maxSamples)
    {
        lock (_gate)
        {
            if (!_hasSpeech || _speechSamples < MinSpeechSamples || _samples.Count < minSamples)
                return null;
            var length = Math.Min(_samples.Count, maxSamples);
            return new VoicePhraseSnapshot(_phrase, _samples.Count, _samples.GetRange(_samples.Count - length, length).ToArray());
        }
    }

    /// <summary>Whether <paramref name="phrase"/> from a <see cref="Snapshot"/> is still being spoken.</summary>
    public bool IsCurrent(int phrase)
    {
        lock (_gate)
            return _hasSpeech && _phrase == phrase;
    }

    /// <summary>Ends the phrase in progress and returns it, or null when it held no speech.</summary>
    public float[]? Drain()
    {
        lock (_gate)
            return DrainLocked();
    }

    /// <summary>
    /// Maps loudness to a meter level relative to the room's quietest moment (<paramref name="floorRms"/>), so a quiet
    /// microphone moves the meter as much as a loud one: the room is 0, 40 dB above it is 1.
    /// </summary>
    public static double ToLevel(double rms, double floorRms)
    {
        if (rms <= 0)
            return 0;
        var aboveRoomDb = 20 * Math.Log10(rms / Math.Max(floorRms, SignalRms));
        return Math.Clamp((aboveRoomDb - MeterHeadroomDb) / MeterRangeDb, 0, 1);
    }

    /// <summary>The room's level: its quietest recent moment, unknown (infinite) until any sound arrived.</summary>
    private double Floor()
    {
        var floor = double.PositiveInfinity;
        foreach (var recent in _recentRms)
            floor = Math.Min(floor, recent);
        return floor;
    }

    private void EndFrame()
    {
        var rms = Math.Sqrt(_frameEnergy / FrameSamples);
        _frameEnergy = 0;
        _frameFill = 0;

        var floor = Floor();
        // Digital silence, from a microphone still starting or muted, says nothing about the room; the first sound
        // does, so a fan heard from the first moment is the floor rather than speech.
        if (rms > SignalRms)
        {
            _recentRms[_recentNext] = rms;
            _recentNext = (_recentNext + 1) % NoiseWindowFrames;
        }

        if (rms >= Math.Max(MinimumSpeechRms, floor * NoiseMargin))
        {
            _hasSpeech = true;
            _speechSamples += FrameSamples;
            _silentSamples = 0;
        }
        else if (_hasSpeech)
        {
            _silentSamples += FrameSamples;
        }
    }

    private float[]? DrainLocked()
    {
        var result = _speechSamples >= MinSpeechSamples ? _samples.ToArray() : null;
        _samples.Clear();
        _hasSpeech = false;
        _speechSamples = 0;
        _silentSamples = 0;
        _phrase++;
        return result;
    }
}

/// <summary>The newest audio of a phrase in progress, for a live preview.</summary>
/// <param name="Phrase">Which phrase this is; see <see cref="VoiceAudioBuffer.IsCurrent"/>.</param>
/// <param name="Length">How many samples the phrase held, including any older than <paramref name="Samples"/>.</param>
/// <param name="Samples">The newest audio of the phrase.</param>
internal sealed record VoicePhraseSnapshot(int Phrase, int Length, float[] Samples);
