using System.Globalization;

namespace Lumi.Mobile.Services;

/// <summary>What happened to one live streaming delta on the phone.</summary>
public enum StreamDeltaDisposition
{
    /// <summary>Extended the visible text directly.</summary>
    Applied,

    /// <summary>Kept until a transcript fetch already in flight lands.</summary>
    Held,

    /// <summary>Applied after the transcript it was waiting for.</summary>
    Replayed,

    /// <summary>Superseded by a newer transcript.</summary>
    Dropped,

    /// <summary>Could not be placed and asked for a transcript fetch.</summary>
    Refetched
}

/// <summary>
/// Counters for what the phone's link to Lumi costs: requests and bytes per route, live frames per
/// event, reconnects and how transcripts were kept current. Cheap enough to always run; read by
/// Settings and written to the log when the app goes to the background.
/// </summary>
public sealed class RemoteTransportStats
{
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly Dictionary<string, (int Count, long Bytes)> _requests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Count, long Bytes)> _frames = new(StringComparer.Ordinal);
    private readonly int[] _deltas = new int[Enum.GetValues<StreamDeltaDisposition>().Length];
    private DateTimeOffset _since;
    private int _streamConnects;
    private int _streamFailures;
    private int _bootstraps;
    private int _bootstrapSectionsReused;
    private int _transcripts;
    private int _transcriptsUnchanged;

    public RemoteTransportStats(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _since = _time.GetUtcNow();
    }

    public void RecordRequest(string route)
    {
        lock (_gate)
        {
            var current = _requests.GetValueOrDefault(route);
            _requests[route] = (current.Count + 1, current.Bytes);
        }
    }

    public void RecordResponseBytes(string route, long bytes)
    {
        lock (_gate)
        {
            var current = _requests.GetValueOrDefault(route);
            _requests[route] = (current.Count, current.Bytes + Math.Max(0, bytes));
        }
    }

    public void RecordFrame(string eventName, long bytes)
    {
        lock (_gate)
        {
            var current = _frames.GetValueOrDefault(eventName);
            _frames[eventName] = (current.Count + 1, current.Bytes + Math.Max(0, bytes));
        }
    }

    public void RecordStreamConnected()
    {
        lock (_gate)
            _streamConnects++;
    }

    public void RecordStreamFailed()
    {
        lock (_gate)
            _streamFailures++;
    }

    public void RecordBootstrap(int reusedSections)
    {
        lock (_gate)
        {
            _bootstraps++;
            _bootstrapSectionsReused += Math.Max(0, reusedSections);
        }
    }

    public void RecordTranscript(bool unchanged)
    {
        lock (_gate)
        {
            _transcripts++;
            if (unchanged)
                _transcriptsUnchanged++;
        }
    }

    public void RecordDelta(StreamDeltaDisposition disposition)
    {
        lock (_gate)
            _deltas[(int)disposition]++;
    }

    public void Reset()
    {
        lock (_gate)
        {
            _requests.Clear();
            _frames.Clear();
            Array.Clear(_deltas);
            _streamConnects = _streamFailures = _bootstraps = _bootstrapSectionsReused = 0;
            _transcripts = _transcriptsUnchanged = 0;
            _since = _time.GetUtcNow();
        }
    }

    public RemoteTransportStatsSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new RemoteTransportStatsSnapshot(
                _time.GetUtcNow() - _since,
                new Dictionary<string, (int Count, long Bytes)>(_requests, StringComparer.Ordinal),
                new Dictionary<string, (int Count, long Bytes)>(_frames, StringComparer.Ordinal),
                Enum.GetValues<StreamDeltaDisposition>().ToDictionary(
                    static disposition => disposition,
                    disposition => _deltas[(int)disposition]),
                _streamConnects,
                _streamFailures,
                _bootstraps,
                _bootstrapSectionsReused,
                _transcripts,
                _transcriptsUnchanged);
        }
    }
}

public sealed record RemoteTransportStatsSnapshot(
    TimeSpan Elapsed,
    IReadOnlyDictionary<string, (int Count, long Bytes)> Requests,
    IReadOnlyDictionary<string, (int Count, long Bytes)> Frames,
    IReadOnlyDictionary<StreamDeltaDisposition, int> Deltas,
    int StreamConnects,
    int StreamFailures,
    int Bootstraps,
    int BootstrapSectionsReused,
    int Transcripts,
    int TranscriptsUnchanged)
{
    public int RequestCount => Requests.Values.Sum(static value => value.Count);

    public long ResponseBytes => Requests.Values.Sum(static value => value.Bytes);

    public int FrameCount => Frames.Values.Sum(static value => value.Count);

    public long FrameBytes => Frames.Values.Sum(static value => value.Bytes);

    public int RequestsTo(string route) => Requests.TryGetValue(route, out var value) ? value.Count : 0;

    /// <summary>A one-line summary for Settings and the log.</summary>
    public string Summary =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Count(RequestCount, "request")}, {FormatBytes(ResponseBytes + FrameBytes)} received, " +
            $"{Count(FrameCount, "live update")}, {Count(Math.Max(0, StreamConnects - 1), "reconnect")}") +
        (Transcripts > 0
            ? string.Create(
                CultureInfo.InvariantCulture,
                $", {TranscriptsUnchanged} of {Count(Transcripts, "chat reload")} already up to date")
            : "");

    /// <summary>A multi-line breakdown for logs and diagnostics.</summary>
    public string Details
    {
        get
        {
            var lines = new List<string>
            {
                string.Create(CultureInfo.InvariantCulture, $"Since {Elapsed:hh\\:mm\\:ss}: {Summary}"),
                string.Create(CultureInfo.InvariantCulture,
                    $"Streams: {StreamConnects} connected, {StreamFailures} failed; " +
                    $"{Bootstraps} bootstraps reused {BootstrapSectionsReused} cached sections")
            };
            lines.AddRange(Requests
                .OrderByDescending(static pair => pair.Value.Count)
                .Select(static pair => string.Create(
                    CultureInfo.InvariantCulture,
                    $"  {pair.Key}: {pair.Value.Count} requests, {FormatBytes(pair.Value.Bytes)}")));
            lines.AddRange(Frames
                .OrderByDescending(static pair => pair.Value.Count)
                .Select(static pair => string.Create(
                    CultureInfo.InvariantCulture,
                    $"  event {pair.Key}: {pair.Value.Count} frames, {FormatBytes(pair.Value.Bytes)}")));
            lines.Add(string.Join(
                ", ",
                Deltas.Select(static pair => string.Create(CultureInfo.InvariantCulture, $"{pair.Key} {pair.Value}"))));
            return string.Join(Environment.NewLine, lines);
        }
    }

    private static string Count(long count, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {noun}{(count == 1 ? "" : "s")}");

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024d:0.#} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024d * 1024):0.#} MB")
    };
}
