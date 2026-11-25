namespace Lumi.Remote.Protocol;

using System.Security.Cryptography;
using System.Text.Json;

/// <summary>Reuses complete, immutable wire rows. The desktop never has to remember what a phone received.</summary>
public static class RemoteTranscriptReuse
{
    private const int KeyLength = 16;
    public const int MaxKeys = 120;

    public static string Key(RemoteTranscriptItem item) =>
        Key(JsonSerializer.SerializeToUtf8Bytes(item, RemoteJsonContext.Default.RemoteTranscriptItem));

    private static string Key(ReadOnlySpan<byte> json) =>
        Convert.ToBase64String(SHA256.HashData(json)
            .AsSpan(0, 12)).Replace('+', '-').Replace('/', '_');

    public static HashSet<string>? Parse(string? value)
    {
        if (value is not { Length: > 0 and <= MaxKeys * (KeyLength + 1) })
            return null;

        var keys = value.Split(',');
        return keys.Length <= MaxKeys && keys.All(key => key.Length == KeyLength
            && key.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
            ? new HashSet<string>(keys, StringComparer.Ordinal)
            : null;
    }

    public static void OmitKnownItems(RemoteTranscript transcript, IReadOnlySet<string> known)
    {
        if (transcript.Turns.Sum(turn => turn.Items.Count) > MaxKeys)
            return;

        foreach (var turn in transcript.Turns)
        {
            var keys = new List<string>(turn.Items.Count);
            var changes = new List<RemoteTranscriptItem>(turn.Items.Count);
            var omittedBytes = 0;
            foreach (var item in turn.Items)
            {
                var json = JsonSerializer.SerializeToUtf8Bytes(item, RemoteJsonContext.Default.RemoteTranscriptItem);
                var key = Key(json);
                keys.Add(key);
                if (known.Contains(key))
                    omittedBytes += json.Length;
                else
                    changes.Add(item);
            }

            if (omittedBytes <= 16 + keys.Count * (KeyLength + 3))
                continue;

            turn.ItemOrder = keys;
            turn.Items = changes;
        }
    }

    public static bool TryRestore(RemoteTranscript transcript, IReadOnlyDictionary<string, RemoteTranscriptItem> known)
    {
        foreach (var turn in transcript.Turns)
        {
            if (turn.ItemOrder is not { } order)
                continue;
            if (order.Count > MaxKeys)
                return false;

            var restored = new List<RemoteTranscriptItem>(order.Count);
            var next = 0;
            foreach (var key in order)
            {
                if (known.TryGetValue(key, out var item))
                    restored.Add(item);
                else if (next < turn.Items.Count && Key(turn.Items[next]) == key)
                    restored.Add(turn.Items[next++]);
                else
                    return false;
            }

            if (next != turn.Items.Count)
                return false;

            turn.Items = restored;
            turn.ItemOrder = null;
        }

        return true;
    }
}
