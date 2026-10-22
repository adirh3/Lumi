using System;
using System.Collections;
using System.Collections.Generic;
using Lumi.Models;

namespace Lumi.Services.Remote;

/// <summary>
/// A chat's transcript by absolute position, for the phone's windowed views. The newest positions
/// are the live messages the desktop holds (its tail, including anything streaming or unsaved); every
/// older position is read from the chat database a page at a time, only when a window selection
/// actually reaches it. Paging back through a 60,000-message chat therefore reads a few hundred
/// messages per request instead of the whole transcript. Read on a background thread.
/// </summary>
internal sealed class RemoteMessageSource : IReadOnlyList<ChatMessage>
{
    private const int PageSize = 256;

    private readonly Func<int, int, List<ChatMessage>> _readStored;
    private readonly int _storedCount;
    private readonly IReadOnlyList<ChatMessage> _tail;
    private readonly Dictionary<int, List<ChatMessage>> _pages = [];

    /// <param name="readStored">Reads stored messages by absolute position: (start, count).</param>
    /// <param name="storedCount">Positions served from storage: the history before the live tail.</param>
    /// <param name="tail">The live messages that follow the stored positions.</param>
    public RemoteMessageSource(Func<int, int, List<ChatMessage>> readStored, int storedCount, IReadOnlyList<ChatMessage> tail)
    {
        _readStored = readStored;
        _storedCount = Math.Max(0, storedCount);
        _tail = tail;
    }

    public int Count => _storedCount + _tail.Count;

    /// <summary>Positions served from storage: everything before the live messages.</summary>
    public int StoredCount => _storedCount;

    /// <summary>Stored positions read so far (diagnostics and tests).</summary>
    public int ReadMessageCount { get; private set; }

    public ChatMessage this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            if (index >= _storedCount)
                return _tail[index - _storedCount];

            var page = index / PageSize;
            if (!_pages.TryGetValue(page, out var messages))
            {
                var start = page * PageSize;
                messages = _readStored(start, Math.Min(PageSize, _storedCount - start));
                ReadMessageCount += messages.Count;
                _pages[page] = messages;
            }

            var offset = index - page * PageSize;
            // Storage changed underneath (the chat was edited meanwhile): report a quiet empty
            // message rather than failing the whole window.
            return offset < messages.Count ? messages[offset] : new ChatMessage { Role = "system", Content = "" };
        }
    }

    /// <summary>The messages in [<paramref name="start"/>, <paramref name="end"/>), in order.</summary>
    public List<ChatMessage> Slice(int start, int end)
    {
        start = Math.Clamp(start, 0, Count);
        end = Math.Clamp(end, start, Count);
        var slice = new List<ChatMessage>(end - start);
        for (var index = start; index < end; index++)
            slice.Add(this[index]);
        return slice;
    }

    /// <summary>The position of the user message that starts the turn holding <paramref name="index"/>.</summary>
    public int FindTurnStart(int index)
    {
        index = Math.Clamp(index, 0, Math.Max(0, Count - 1));
        while (index > 0 && this[index].Role != "user")
            index--;
        return index;
    }

    /// <summary>The position after the turn holding <paramref name="index"/> (its next user message, or the end).</summary>
    public int FindTurnEnd(int index)
    {
        var end = index + 1;
        while (end < Count && this[end].Role != "user")
            end++;
        return end;
    }

    public IEnumerator<ChatMessage> GetEnumerator()
    {
        for (var index = 0; index < Count; index++)
            yield return this[index];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
