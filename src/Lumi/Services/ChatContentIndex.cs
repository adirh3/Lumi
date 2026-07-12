using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Models;
using StrataSearch;

namespace Lumi.Services;

/// <summary>
/// Maintains a bounded, normalized content blob for every chat so that full-text chat
/// search covers the entire history cheaply — not just the handful of most-recent chats.
///
/// Memory is bounded by capping the indexed text per chat. The index is warmed in the
/// background, can be persisted to disk so restarts don't re-read the whole history, and
/// is kept fresh for the open chat (which always carries its messages in memory).
/// </summary>
public sealed class ChatContentIndex
{
    private const int MaxUserIndexedChars = 12_000;
    private const int MaxAssistantIndexedChars = 8_000;
    private const string PersistMagic = "LCI3";
    private const int MaxPersistedEntries = 5_000_000;

    private readonly Func<Chat, ChatSearchSnapshot> _snapshotProvider;
    private readonly Action<Guid>? _releaseSnapshot;
    private readonly Func<Guid, DateTimeOffset?>? _fileTimestampProvider;
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Entry> _entries = [];

    public ChatContentIndex(
        Func<Chat, ChatSearchSnapshot> snapshotProvider,
        Action<Guid>? releaseSnapshot = null,
        Func<Guid, DateTimeOffset?>? fileTimestampProvider = null)
    {
        _snapshotProvider = snapshotProvider ?? throw new ArgumentNullException(nameof(snapshotProvider));
        _releaseSnapshot = releaseSnapshot;
        _fileTimestampProvider = fileTimestampProvider;
    }

    /// <summary>Number of chats currently held in the index.</summary>
    public int Count
    {
        get
        {
            lock (_sync)
                return _entries.Count;
        }
    }

    /// <summary>True when a content entry is already cached for the chat.</summary>
    public bool IsCached(Guid chatId)
    {
        lock (_sync)
            return _entries.ContainsKey(chatId);
    }

    /// <summary>
    /// Returns the content entry for a chat, building it from the snapshot provider when allowed.
    /// Loaded chats (messages already in memory) are always refreshed cheaply; cold chats are
    /// only read from the provider when <paramref name="allowBuild"/> is true.
    /// </summary>
    public Entry? GetEntry(Chat chat, bool allowBuild)
    {
        ArgumentNullException.ThrowIfNull(chat);

        var isLoaded = chat.Messages.Count > 0;
        if (isLoaded)
            return BuildEntry(chat, releaseAfter: false);

        lock (_sync)
        {
            if (_entries.TryGetValue(chat.Id, out var cached))
                return cached;
        }

        if (!allowBuild)
            return null;

        return BuildEntry(chat, releaseAfter: true);
    }

    /// <summary>Removes a chat's entry, forcing a rebuild on next access/warm (e.g., after a save).</summary>
    public void Invalidate(Guid chatId)
    {
        lock (_sync)
            _entries.Remove(chatId);
    }

    /// <summary>Removes a chat's entry entirely (e.g., after deletion).</summary>
    public void Remove(Guid chatId) => Invalidate(chatId);

    /// <summary>
    /// Builds entries for any cold chats that are not yet cached, throttled to avoid hammering
    /// the disk. Cached entries restored from a persisted index are revalidated against the
    /// chat file's current timestamp and rebuilt if stale (e.g., edited by another instance or
    /// after an ungraceful shutdown). Intended to run on a background thread after startup.
    /// </summary>
    public async Task WarmAsync(IReadOnlyList<Chat> chats, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chats);

        // Warm most-recent chats first so the freshest history becomes searchable soonest.
        var ordered = chats
            .Where(static chat => chat.Messages.Count == 0)
            .OrderByDescending(static chat => chat.UpdatedAt)
            .ToArray();

        var processed = 0;
        foreach (var chat in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsCached(chat.Id) || IsStale(chat.Id))
                BuildEntry(chat, releaseAfter: true);

            if (++processed % 32 == 0)
                await Task.Delay(12, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Drops entries for chats that no longer exist so the index doesn't leak deleted chats.</summary>
    public void Prune(IEnumerable<Guid> liveChatIds)
    {
        var live = liveChatIds as HashSet<Guid> ?? [.. liveChatIds];
        lock (_sync)
        {
            var stale = _entries.Keys.Where(id => !live.Contains(id)).ToArray();
            foreach (var id in stale)
                _entries.Remove(id);
        }
    }

    /// <summary>
    /// True when a cached entry's source chat file has a different last-write time than when the
    /// entry was built (e.g. edited by another Lumi instance or after an ungraceful shutdown).
    /// Always false when no timestamp provider is configured, preserving cache-only behavior.
    /// </summary>
    private bool IsStale(Guid chatId)
    {
        if (_fileTimestampProvider is null)
            return false;

        long cachedTicks;
        lock (_sync)
        {
            if (!_entries.TryGetValue(chatId, out var entry))
                return true;
            cachedTicks = entry.SourceTicks;
        }

        var currentTicks = _fileTimestampProvider(chatId)?.UtcTicks ?? 0L;
        return currentTicks != cachedTicks;
    }

    private Entry BuildEntry(Chat chat, bool releaseAfter)
    {
        // Capture the source timestamp BEFORE reading content so that any change racing the read
        // leaves the entry tagged with an older time, forcing a rebuild on the next warm.
        var sourceTicks = _fileTimestampProvider?.Invoke(chat.Id)?.UtcTicks ?? 0L;
        var snapshot = _snapshotProvider(chat);
        var entry = CreateEntry(snapshot, sourceTicks);

        lock (_sync)
            _entries[chat.Id] = entry;

        if (releaseAfter)
            _releaseSnapshot?.Invoke(chat.Id);

        return entry;
    }

    private static Entry CreateEntry(ChatSearchSnapshot snapshot, long sourceTicks)
    {
        var userText = ConcatenateBalanced(
            snapshot.Messages,
            MaxUserIndexedChars,
            static message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase));
        var assistantText = ConcatenateBalanced(
            snapshot.Messages,
            MaxAssistantIndexedChars,
            static message => IsAssistantSearchableRole(message.Role));
        var preparedUser = SearchText.Create(userText);
        var preparedAssistant = SearchText.Create(assistantText);

        return new Entry(
            snapshot.Version,
            preparedUser.Normalized,
            preparedUser.Compact,
            preparedAssistant.Normalized,
            preparedAssistant.Compact,
            sourceTicks);
    }

    private static bool IsAssistantSearchableRole(string? role)
    {
        return string.IsNullOrWhiteSpace(role)
               || string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)
               || string.Equals(role, "error", StringComparison.OrdinalIgnoreCase);
    }

    private static string ConcatenateBalanced(
        IReadOnlyList<ChatSearchMessage> messages,
        int maxChars,
        Func<ChatSearchMessage, bool> include)
    {
        if (messages.Count == 0 || maxChars <= 0)
            return "";

        var eligible = messages
            .Where(message => include(message) && !string.IsNullOrWhiteSpace(message.Text))
            .Select(static message => message.Text)
            .ToArray();
        if (eligible.Length == 0)
            return "";

        long totalLength = Math.Max(0, eligible.Length - 1);
        foreach (var text in eligible)
            totalLength += text.Length;

        if (totalLength <= maxChars)
            return string.Join('\n', eligible);

        var headBudget = (maxChars * 2) / 3;
        var tailBudget = maxChars - headBudget - 1;
        return $"{BuildHead(eligible, headBudget)}\n{BuildTail(eligible, tailBudget)}";
    }

    private static string BuildHead(IReadOnlyList<string> texts, int maxChars)
    {
        var builder = new StringBuilder(maxChars);
        for (var index = 0; index < texts.Count && builder.Length < maxChars; index++)
        {
            if (index > 0)
                builder.Append('\n');

            var remaining = maxChars - builder.Length;
            if (remaining <= 0)
                break;

            var text = texts[index];
            builder.Append(text.Length <= remaining ? text : text[..remaining]);
        }

        return builder.ToString();
    }

    private static string BuildTail(IReadOnlyList<string> texts, int maxChars)
    {
        var reversedParts = new List<string>();
        var remaining = maxChars;

        for (var index = texts.Count - 1; index >= 0 && remaining > 0; index--)
        {
            var text = texts[index];
            if (text.Length >= remaining)
            {
                reversedParts.Add(text[^remaining..]);
                remaining = 0;
                break;
            }

            reversedParts.Add(text);
            remaining -= text.Length;

            if (index > 0 && remaining > 0)
            {
                reversedParts.Add("\n");
                remaining--;
            }
        }

        reversedParts.Reverse();
        return string.Concat(reversedParts);
    }

    /// <summary>
    /// A single chat's bounded, normalized content. <see cref="Normalized"/> keeps token
    /// boundaries (single-spaced); <see cref="Compact"/> removes them for cross-separator matching.
    /// </summary>
    public sealed class Entry
    {
        public Entry(
            string version,
            string userNormalized,
            string userCompact,
            string assistantNormalized,
            string assistantCompact,
            long sourceTicks = 0)
        {
            Version = version;
            UserNormalized = userNormalized;
            UserCompact = userCompact;
            AssistantNormalized = assistantNormalized;
            AssistantCompact = assistantCompact;
            SourceTicks = sourceTicks;
        }

        public string Version { get; }
        public string UserNormalized { get; }
        public string UserCompact { get; }
        public string AssistantNormalized { get; }
        public string AssistantCompact { get; }

        /// <summary>UTC ticks of the source chat file's last-write time when this entry was built (0 if unknown).</summary>
        public long SourceTicks { get; }
        public bool IsEmpty => UserNormalized.Length == 0 && AssistantNormalized.Length == 0;

        /// <summary>Cheap gate: true when any query term appears in the content (substring or compact).</summary>
        public bool MayMatch(SearchQuery query)
        {
            if (IsEmpty || query.IsEmpty)
                return false;

            foreach (var term in query.Terms)
            {
                if (FieldMayMatch(UserNormalized, UserCompact, term)
                    || FieldMayMatch(AssistantNormalized, AssistantCompact, term))
                {
                    return true;
                }
            }

            return false;
        }

        public IReadOnlyList<PreparedSearchField> ToContentFields(
            double userWeight,
            double assistantWeight)
        {
            if (UserNormalized.Length > 0 && AssistantNormalized.Length > 0)
            {
                return
                [
                    new PreparedSearchField(UserNormalized, userWeight, SearchFieldKind.Content),
                    new PreparedSearchField(AssistantNormalized, assistantWeight, SearchFieldKind.Content)
                ];
            }

            if (UserNormalized.Length > 0)
                return [new PreparedSearchField(UserNormalized, userWeight, SearchFieldKind.Content)];

            return AssistantNormalized.Length > 0
                ? [new PreparedSearchField(AssistantNormalized, assistantWeight, SearchFieldKind.Content)]
                : Array.Empty<PreparedSearchField>();
        }

        private static bool FieldMayMatch(string normalized, string compact, SearchText term)
        {
            return (term.Normalized.Length > 0 && normalized.Contains(term.Normalized, StringComparison.Ordinal))
                   || (term.Compact.Length > 0 && compact.Contains(term.Compact, StringComparison.Ordinal));
        }
    }

    // ── Persistence ──────────────────────────────────────────────────────────

    /// <summary>Serializes the index to disk so a restart can skip re-reading the full history.</summary>
    public void Save(string path)
    {
        KeyValuePair<Guid, Entry>[] snapshot;
        lock (_sync)
            snapshot = [.. _entries];

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // A unique temp name keeps concurrent saves (e.g. background warm completion vs. shutdown)
        // from colliding on a shared ".tmp" file and losing a write.
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(PersistMagic);
                writer.Write(snapshot.Length);
                foreach (var (id, entry) in snapshot)
                {
                    writer.Write(id.ToByteArray());
                    writer.Write(entry.Version);
                    writer.Write(entry.SourceTicks);
                    writer.Write(entry.UserNormalized);
                    writer.Write(entry.AssistantNormalized);
                }
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                    // Best-effort cleanup of the temp file.
                }
            }
        }
    }

    /// <summary>Loads a previously persisted index. Returns the number of entries loaded.</summary>
    public int Load(string path)
    {
        if (!File.Exists(path))
            return 0;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.UTF8);

            if (reader.ReadString() != PersistMagic)
                return 0;

            var count = reader.ReadInt32();
            if (count < 0 || count > MaxPersistedEntries)
                return 0;

            var loaded = new Dictionary<Guid, Entry>(Math.Min(count, 4096));
            for (var i = 0; i < count; i++)
            {
                var id = new Guid(reader.ReadBytes(16));
                var version = reader.ReadString();
                var sourceTicks = reader.ReadInt64();
                var userNormalized = reader.ReadString();
                var assistantNormalized = reader.ReadString();
                loaded[id] = new Entry(
                    version,
                    userNormalized,
                    StripSpaces(userNormalized),
                    assistantNormalized,
                    StripSpaces(assistantNormalized),
                    sourceTicks);
            }

            lock (_sync)
            {
                foreach (var (id, entry) in loaded)
                    _entries[id] = entry;
            }

            return loaded.Count;
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or FormatException
                                     or ArgumentException or OverflowException)
        {
            // A corrupt, truncated, or partial index just means we re-warm from scratch.
            return 0;
        }
    }

    private static string StripSpaces(string normalized)
    {
        if (normalized.IndexOf(' ') < 0)
            return normalized;

        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (character != ' ')
                builder.Append(character);
        }

        return builder.ToString();
    }
}
