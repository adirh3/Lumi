using System.Text;

namespace Lumi.Remote.Protocol;

/// <summary>
/// One Server-Sent Events frame. Both the desktop writer and the mobile reader go through this
/// type so the framing rules (event name, single-line escaped data, blank-line terminator) can
/// only be defined once.
/// </summary>
public readonly record struct RemoteEventFrame(string Event, string Data)
{
    /// <summary>Renders the frame in SSE wire format, including the terminating blank line.</summary>
    public string ToWire()
    {
        var builder = new StringBuilder();
        builder.Append("event: ").Append(Event).Append('\n');

        // SSE is line-oriented: every physical line of the payload needs its own `data:` field.
        foreach (var line in Data.Split('\n'))
            builder.Append("data: ").Append(line.TrimEnd('\r')).Append('\n');

        builder.Append('\n');
        return builder.ToString();
    }

    /// <summary>Incrementally reassembles frames from raw SSE lines.</summary>
    public sealed class Reader
    {
        private readonly StringBuilder _data = new();
        private string _event = "message";
        private int _dataBytes;

        /// <summary>
        /// Feeds one line. Returns a completed frame when the terminating blank line arrives,
        /// otherwise <c>null</c>.
        /// </summary>
        public RemoteEventFrame? Push(string line)
        {
            if (line.Length == 0)
            {
                if (_data.Length == 0)
                    return null;

                var frame = new RemoteEventFrame(_event, _data.ToString());
                _data.Clear();
                _dataBytes = 0;
                _event = "message";
                return frame;
            }

            if (line[0] == ':')
                return null; // comment / keep-alive

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                var eventName = line[6..].Trim();
                if (eventName.Length > 128)
                    throw new InvalidDataException("SSE event name is too large.");
                _event = eventName;
                return null;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var value = line[5..].TrimStart(' ');
                var addedBytes = Encoding.UTF8.GetByteCount(value) + (_data.Length > 0 ? 1 : 0);
                if (_dataBytes + addedBytes > RemoteProtocol.MaxSseFrameBytes)
                    throw new InvalidDataException("SSE frame is too large.");

                if (_data.Length > 0)
                    _data.Append('\n');
                _data.Append(value);
                _dataBytes += addedBytes;
            }

            return null;
        }
    }
}
