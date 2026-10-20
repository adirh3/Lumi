using System;

namespace Lumi.Services.Remote;

/// <summary>
/// A small incremental FNV-1a 64-bit hash for change detection on the remote wire (snapshot section
/// versions, chat rows, status payloads, transcript versions). It is not a security primitive.
/// </summary>
internal struct RemoteContentHash
{
    private const ulong OffsetBasis = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    private ulong _value;
    private bool _started;

    public readonly ulong Value => _started ? _value : OffsetBasis;

    public static ulong Of(ReadOnlySpan<byte> bytes)
    {
        var hash = new RemoteContentHash();
        hash.Add(bytes);
        return hash.Value;
    }

    public static string Format(ulong value) => value.ToString("x16");

    public void Add(ReadOnlySpan<byte> bytes)
    {
        Start();
        foreach (var value in bytes)
        {
            _value ^= value;
            _value *= Prime;
        }
    }

    public void Add(string? text)
    {
        Start();
        if (text is null)
        {
            Mix(0xFF);
            Mix(0xFF);
            return;
        }

        Add(text.Length);
        foreach (var character in text)
        {
            Mix((byte)character);
            Mix((byte)(character >> 8));
        }
    }

    public void Add(long value)
    {
        Start();
        for (var shift = 0; shift < 64; shift += 8)
            Mix((byte)(value >> shift));
    }

    public void Add(bool value) => Add(value ? 1L : 0L);

    public void Add(Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes);
        Add(bytes);
    }

    public void Add(Guid? value)
    {
        if (value is { } guid)
            Add(guid);
        else
            Add(-1L);
    }

    private void Start()
    {
        if (_started)
            return;

        _value = OffsetBasis;
        _started = true;
    }

    private void Mix(byte value)
    {
        _value ^= value;
        _value *= Prime;
    }
}
