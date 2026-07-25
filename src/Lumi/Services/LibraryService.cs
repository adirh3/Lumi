using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Models;

namespace Lumi.Services;

/// <summary>Coarse artifact buckets the Library filters on. Kept small enough to render as chips.</summary>
public enum LibraryArtifactKind
{
    Image,
    Document,
    Sheet,
    Slides,
    Code,
    Media,
    Archive,
    Link,
    Worktree,
    Other
}

/// <summary>Where an artifact came from — a user attachment, a Lumi deliverable, or a cited web source.</summary>
public enum LibraryArtifactOrigin
{
    Sent,
    Created,
    Referenced
}

/// <summary>
/// A single artifact surfaced in the Library: a file the user sent, a file Lumi produced, or a web
/// source a chat cited. Artifacts are derived from chat history on demand — never persisted.
/// </summary>
public sealed class LibraryArtifact
{
    /// <summary>Normalized dedupe key (full path or absolute URL, case-insensitive).</summary>
    public required string Key { get; init; }

    /// <summary>Absolute file path, or the URL for <see cref="LibraryArtifactKind.Link"/> artifacts.</summary>
    public required string Location { get; init; }

    public required string Name { get; init; }
    public required string Extension { get; init; }
    public required LibraryArtifactKind Kind { get; init; }
    public required LibraryArtifactOrigin Origin { get; init; }

    /// <summary>Chat the artifact was most recently seen in.</summary>
    public required Guid ChatId { get; init; }
    public required string ChatTitle { get; init; }
    public Guid? ProjectId { get; init; }
    public string? ProjectName { get; init; }

    /// <summary>Timestamp of the most recent message referencing this artifact.</summary>
    public required DateTimeOffset LastSeen { get; init; }
    public required DateTimeOffset FirstSeen { get; init; }

    /// <summary>Number of distinct chats that reference this artifact.</summary>
    public int ChatCount { get; init; } = 1;

    /// <summary>Web sources carry an optional snippet; files leave this null.</summary>
    public string? Description { get; init; }

    public bool IsLink => Kind == LibraryArtifactKind.Link;

    /// <summary>
    /// True when every field the Library renders matches. A progressive scan re-merges the same
    /// references repeatedly, so this lets rows skip a no-op notification storm.
    /// </summary>
    public bool HasSameDisplayData(LibraryArtifact other)
        => Kind == other.Kind
           && Origin == other.Origin
           && Exists == other.Exists
           && SizeBytes == other.SizeBytes
           && ChatCount == other.ChatCount
           && ChatId == other.ChatId
           && LastSeen == other.LastSeen
           && FirstSeen == other.FirstSeen
           && ProjectId == other.ProjectId
           && string.Equals(Name, other.Name, StringComparison.Ordinal)
           && string.Equals(Location, other.Location, StringComparison.Ordinal)
           && string.Equals(ChatTitle, other.ChatTitle, StringComparison.Ordinal)
           && string.Equals(ProjectName, other.ProjectName, StringComparison.Ordinal)
           && string.Equals(Description, other.Description, StringComparison.Ordinal);

    /// <summary>False when a file artifact no longer exists on disk (links are always considered available).</summary>
    public bool Exists { get; init; }

    public long SizeBytes { get; init; }
}

/// <summary>Partial scan result: what has been found so far and how far the scan has progressed.</summary>
/// <param name="Artifacts">Merged artifacts discovered up to this point, newest first.</param>
/// <param name="ChatsScanned">Chats visited so far.</param>
/// <param name="ChatsTotal">Total chats the scan will visit.</param>
public sealed record LibraryScanProgress(
    IReadOnlyList<LibraryArtifact> Artifacts,
    int ChatsScanned,
    int ChatsTotal);

/// <summary>
/// Scans every chat's message history and aggregates the files sent by the user, the files Lumi
/// produced (<c>announce_file</c>), and the web sources chats cited. Results are cached per chat and
/// invalidated by the chat's persisted-message signature, so repeat scans only re-read what changed.
/// </summary>
public sealed class LibraryService
{
    private readonly DataStore _dataStore;
    private readonly Dictionary<Guid, CachedChatScan> _cache = [];
    private readonly object _cacheSync = new();

    public LibraryService(DataStore dataStore)
    {
        ArgumentNullException.ThrowIfNull(dataStore);
        _dataStore = dataStore;
    }

    /// <summary>Drops all cached per-chat scans (e.g. after chat history is wiped).</summary>
    public void InvalidateAll()
    {
        lock (_cacheSync)
            _cache.Clear();
    }

    /// <summary>Drops the cached scan for a single chat.</summary>
    public void Invalidate(Guid chatId)
    {
        lock (_cacheSync)
            _cache.Remove(chatId);
    }

    /// <summary>
    /// Snapshots the chat and project lists, then scans them. Must be called from the UI thread
    /// (<see cref="AppData.Chats"/> is a plain <see cref="List{T}"/> and is mutated there); the
    /// scan itself then runs off it. Background callers should capture their own snapshot on the
    /// UI thread and use the overload that takes it.
    /// </summary>
    public Task<IReadOnlyList<LibraryArtifact>> ScanAsync(
        IProgress<LibraryScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => ScanAsync(
            _dataStore.Data.Chats.ToArray(),
            _dataStore.Data.Projects.ToArray(),
            progress,
            cancellationToken);

    /// <summary>
    /// Builds the full artifact list, newest first. Reads persisted chat files, so callers should
    /// await this off the UI thread. Chats are visited newest-first and <paramref name="progress"/>
    /// receives partial results as they accumulate, so a large history fills the Library
    /// progressively instead of blocking behind a single long scan.
    /// </summary>
    /// <param name="chats">
    /// Snapshot of the chats to scan, captured by the caller on the UI thread. Taking it here
    /// instead would enumerate the live list from a background thread, and a chat added mid-scan
    /// (a background job finishing, say) would fault the whole scan with "collection was modified".
    /// </param>
    /// <param name="projects">Snapshot of the projects, captured alongside <paramref name="chats"/>.</param>
    public async Task<IReadOnlyList<LibraryArtifact>> ScanAsync(
        IReadOnlyList<Chat> chats,
        IReadOnlyList<Project> projects,
        IProgress<LibraryScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chats);
        ArgumentNullException.ThrowIfNull(projects);

        var ordered = chats
            .OrderByDescending(chat => chat.UpdatedAt)
            .ToList();
        var projectNames = new Dictionary<Guid, string>();
        foreach (var project in projects)
            projectNames[project.Id] = project.Name;

        var references = new List<ArtifactReference>();
        var liveChatIds = new HashSet<Guid>();
        var fileStats = new Dictionary<string, FileStat>(StringComparer.OrdinalIgnoreCase);
        var scannedChats = 0;
        var lastReport = Stopwatch.StartNew();
        var reportedAny = false;

        foreach (var chat in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            liveChatIds.Add(chat.Id);
            scannedChats++;

            // Read straight off the chat record, so it costs nothing even when the message cache hits.
            if (BuildWorktreeReference(chat) is { } worktree)
                references.Add(worktree);

            var signature = BuildChatSignature(chat);
            if (TryGetCached(chat.Id, signature, out var cached))
            {
                references.AddRange(cached);
                continue;
            }

            IReadOnlyList<ChatMessage> messages;
            try
            {
                messages = await _dataStore.ReadChatMessagesAsync(chat, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                continue;
            }

            var scanned = ExtractReferences(chat, messages);
            StoreCached(chat.Id, signature, scanned);
            references.AddRange(scanned);

            if (progress is null || references.Count == 0)
                continue;

            // Republishing rebuilds the whole gallery on the UI thread, so reports are spaced by
            // wall-clock time. The first batch is published eagerly so the page fills immediately.
            var interval = reportedAny ? ProgressInterval : FirstProgressInterval;
            if (lastReport.Elapsed < interval)
                continue;

            lastReport.Restart();
            reportedAny = true;
            progress.Report(new LibraryScanProgress(Merge(references, projectNames, fileStats), scannedChats, ordered.Count));
        }

        PruneCache(liveChatIds);

        return Merge(references, projectNames, fileStats);
    }

    /// <summary>
    /// Emits the git worktree a chat created, if it declared one. Worktrees are the heaviest thing a
    /// chat leaves behind and are otherwise only findable by remembering which chat made them, so the
    /// Library lists them next to the files. Existence is resolved later, during the merge.
    /// </summary>
    internal static ArtifactReference? BuildWorktreeReference(Chat chat)
    {
        var path = chat.WorktreePath;
        if (string.IsNullOrWhiteSpace(path))
            return null;

        // Normalised so "…\wt" and "…\wt\" are one artifact rather than two.
        var trimmed = path.Trim();
        var normalized = trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (normalized.Length == 0 || normalized.EndsWith(':'))
            normalized = trimmed;

        var name = Path.GetFileName(normalized);
        if (string.IsNullOrEmpty(name))
            name = normalized;

        var chatTitle = string.IsNullOrWhiteSpace(chat.Title) ? Localization.Loc.Library_UntitledChat : chat.Title;

        return new ArtifactReference(
            Key: normalized.ToLowerInvariant(),
            Location: normalized,
            Name: name,
            Extension: string.Empty,
            Kind: LibraryArtifactKind.Worktree,
            Origin: LibraryArtifactOrigin.Created,
            ChatId: chat.Id,
            ChatTitle: chatTitle,
            ProjectId: chat.ProjectId,
            Timestamp: chat.UpdatedAt,
            Description: null);
    }

    /// <summary>Delay before the first partial result, long enough to collect a full screen of cards.</summary>
    private static readonly TimeSpan FirstProgressInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Minimum spacing between partial results once the gallery already has content.</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(700);

    /// <summary>
    /// Pure aggregation step: collapses duplicate references, resolves on-disk metadata, and returns
    /// the newest-first artifact list. Separated from I/O so it can be unit-tested directly.
    /// </summary>
    /// <param name="fileStats">
    /// Optional cache of resolved on-disk metadata, shared across the progress reports of one scan.
    /// A scan merges the whole corpus on every report, so without it each report re-stats every
    /// file it has seen so far - tens of thousands of redundant syscalls over a large history.
    /// </param>
    internal static IReadOnlyList<LibraryArtifact> Merge(
        IReadOnlyList<ArtifactReference> references,
        IReadOnlyDictionary<Guid, string> projectNames,
        Dictionary<string, FileStat>? fileStats = null)
    {
        var merged = new Dictionary<string, MergedReference>(StringComparer.OrdinalIgnoreCase);

        foreach (var reference in references)
        {
            if (merged.TryGetValue(reference.Key, out var existing))
            {
                existing.Absorb(reference);
                continue;
            }

            merged[reference.Key] = new MergedReference(reference);
        }

        var artifacts = new List<LibraryArtifact>(merged.Count);
        foreach (var entry in merged.Values)
        {
            var newest = entry.Newest;
            var exists = entry.IsLink;
            long size = 0;

            if (!entry.IsLink)
            {
                if (fileStats is not null && fileStats.TryGetValue(entry.Key, out var cachedStat))
                {
                    exists = cachedStat.Exists;
                    size = cachedStat.SizeBytes;
                }
                else
                {
                    (exists, size) = StatFile(newest.Location, newest.Kind);
                    fileStats?.Add(entry.Key, new FileStat(exists, size));
                }
            }

            string? projectName = null;
            if (newest.ProjectId is Guid projectId && projectNames.TryGetValue(projectId, out var name))
                projectName = name;

            artifacts.Add(new LibraryArtifact
            {
                Key = entry.Key,
                Location = newest.Location,
                Name = newest.Name,
                Extension = newest.Extension,
                Kind = newest.Kind,
                Origin = entry.Oldest.Origin,
                ChatId = newest.ChatId,
                ChatTitle = newest.ChatTitle,
                ProjectId = newest.ProjectId,
                ProjectName = projectName,
                LastSeen = newest.Timestamp,
                FirstSeen = entry.Oldest.Timestamp,
                ChatCount = entry.ChatIds.Count,
                Description = newest.Description,
                Exists = exists,
                SizeBytes = size
            });
        }

        // Ties are broken on the key so the order is deterministic. Without it, the ~dozens of
        // progressive re-merges during one scan can reshuffle artifacts that share a timestamp
        // (several attachments on one message), making rows visibly jump while the scan runs.
        artifacts.Sort(static (left, right) =>
        {
            var byRecency = right.LastSeen.CompareTo(left.LastSeen);
            return byRecency != 0 ? byRecency : string.CompareOrdinal(left.Key, right.Key);
        });
        return artifacts;
    }

    /// <summary>Resolved on-disk metadata for one artifact location.</summary>
    internal readonly record struct FileStat(bool Exists, long SizeBytes);

    /// <summary>
    /// Resolves whether an artifact is still on disk, and how big it is. Worktrees are directories,
    /// so they are probed as such and never measured - sizing one means walking a whole checkout.
    /// </summary>
    private static (bool Exists, long SizeBytes) StatFile(string location, LibraryArtifactKind kind)
    {
        try
        {
            if (kind == LibraryArtifactKind.Worktree)
                return (Directory.Exists(location), 0);

            var info = new FileInfo(location);
            return info.Exists ? (true, info.Length) : (false, 0);
        }
        catch
        {
            return (false, 0);
        }
    }

    /// <summary>Extracts every artifact reference carried by a chat's messages.</summary>
    internal static List<ArtifactReference> ExtractReferences(Chat chat, IReadOnlyList<ChatMessage> messages)
    {
        var references = new List<ArtifactReference>();
        if (messages.Count == 0)
            return references;

        var chatTitle = string.IsNullOrWhiteSpace(chat.Title) ? Localization.Loc.Library_UntitledChat : chat.Title;

        foreach (var message in messages)
        {
            foreach (var attachment in message.Attachments)
            {
                if (CreateFileReference(chat, chatTitle, message.Timestamp, attachment, LibraryArtifactOrigin.Sent) is { } sent)
                    references.Add(sent);
            }

            if (string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase)
                && string.Equals(message.ToolName, "announce_file", StringComparison.Ordinal))
            {
                var announced = ToolDisplayHelper.ExtractJsonField(message.Content, "filePath");
                if (CreateFileReference(chat, chatTitle, message.Timestamp, announced, LibraryArtifactOrigin.Created) is { } created)
                    references.Add(created);
            }

            foreach (var source in message.Sources)
            {
                if (CreateLinkReference(chat, chatTitle, message.Timestamp, source) is { } link)
                    references.Add(link);
            }
        }

        return references;
    }

    private static ArtifactReference? CreateFileReference(
        Chat chat,
        string chatTitle,
        DateTimeOffset timestamp,
        string? rawPath,
        LibraryArtifactOrigin origin)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return null;

        var path = rawPath.Trim();
        try
        {
            if (!Path.IsPathRooted(path))
                return null;
            path = Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }

        var name = ToolDisplayHelper.GetDisplayFileName(path);
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var extension = Path.GetExtension(path) ?? "";

        return new ArtifactReference(
            Key: path,
            Location: path,
            Name: name,
            Extension: extension,
            Kind: ClassifyExtension(extension),
            Origin: origin,
            ChatId: chat.Id,
            ChatTitle: chatTitle,
            ProjectId: chat.ProjectId,
            Timestamp: timestamp,
            Description: null);
    }

    private static ArtifactReference? CreateLinkReference(
        Chat chat,
        string chatTitle,
        DateTimeOffset timestamp,
        SearchSource source)
    {
        if (source is null
            || string.IsNullOrWhiteSpace(source.Url)
            || !Uri.TryCreate(source.Url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        // Citation sources frequently carry the bare host as their title, which would render dozens of
        // identical-looking cards for one busy domain. Fall back to the readable URL in that case.
        var rawTitle = source.Title?.Trim() ?? "";
        var title = rawTitle.Length == 0 || IsHostName(rawTitle, uri)
            ? DescribeUrl(uri)
            : rawTitle;

        return new ArtifactReference(
            Key: uri.AbsoluteUri,
            Location: uri.AbsoluteUri,
            Name: title,
            Extension: uri.Host,
            Kind: LibraryArtifactKind.Link,
            Origin: LibraryArtifactOrigin.Referenced,
            ChatId: chat.Id,
            ChatTitle: chatTitle,
            ProjectId: chat.ProjectId,
            Timestamp: timestamp,
            Description: string.IsNullOrWhiteSpace(source.Snippet) ? null : source.Snippet.Trim());
    }

    private static bool IsHostName(string candidate, Uri uri) =>
        string.Equals(candidate, uri.Host, StringComparison.OrdinalIgnoreCase)
        || string.Equals(candidate, StripWww(uri.Host), StringComparison.OrdinalIgnoreCase);

    private static string StripWww(string host) =>
        host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;

    /// <summary>
    /// Readable stand-in for an untitled web source. Without the path every card from a busy domain
    /// would render as the same host name, which makes the gallery impossible to scan.
    /// </summary>
    internal static string DescribeUrl(Uri uri)
    {
        var host = StripWww(uri.Host);
        var path = uri.AbsolutePath.Trim('/');

        if (path.Length == 0)
            return string.IsNullOrEmpty(uri.Query) ? host : host + uri.Query;

        try
        {
            path = Uri.UnescapeDataString(path);
        }
        catch (UriFormatException)
        {
            // Keep the escaped form when the path is not decodable.
        }

        return $"{host}/{path}";
    }

    /// <summary>Maps a file extension onto the coarse bucket the Library filters by.</summary>
    internal static LibraryArtifactKind ClassifyExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return LibraryArtifactKind.Other;

        return extension.ToLowerInvariant() switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".ico" or ".tiff" or ".tif"
                or ".svg" or ".heic" or ".avif" => LibraryArtifactKind.Image,
            ".pdf" or ".doc" or ".docx" or ".odt" or ".rtf" or ".txt" or ".md" or ".markdown"
                or ".epub" or ".pages" => LibraryArtifactKind.Document,
            ".xls" or ".xlsx" or ".xlsm" or ".csv" or ".tsv" or ".ods" or ".numbers" => LibraryArtifactKind.Sheet,
            ".ppt" or ".pptx" or ".odp" or ".key" => LibraryArtifactKind.Slides,
            ".cs" or ".js" or ".mjs" or ".ts" or ".tsx" or ".jsx" or ".py" or ".go" or ".rs" or ".java"
                or ".c" or ".cpp" or ".h" or ".hpp" or ".rb" or ".php" or ".swift" or ".kt" or ".sql"
                or ".sh" or ".bash" or ".ps1" or ".psm1" or ".bat" or ".cmd" or ".xaml" or ".axaml"
                or ".html" or ".htm" or ".css" or ".scss" or ".json" or ".xml" or ".yaml" or ".yml"
                or ".toml" or ".ini" => LibraryArtifactKind.Code,
            ".mp3" or ".wav" or ".flac" or ".m4a" or ".ogg" or ".aac" or ".mp4" or ".mov" or ".avi"
                or ".mkv" or ".webm" or ".wmv" => LibraryArtifactKind.Media,
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" or ".xz" => LibraryArtifactKind.Archive,
            _ => LibraryArtifactKind.Other
        };
    }

    private string BuildChatSignature(Chat chat)
    {
        if (chat.Messages.Count > 0)
            return $"mem:{chat.Messages.Count}:{chat.UpdatedAt.Ticks}";

        var timestamp = _dataStore.GetChatFileTimestamp(chat.Id);
        return $"file:{timestamp?.Ticks ?? 0}:{chat.MessageCount}";
    }

    private bool TryGetCached(Guid chatId, string signature, out List<ArtifactReference> references)
    {
        lock (_cacheSync)
        {
            if (_cache.TryGetValue(chatId, out var cached)
                && string.Equals(cached.Signature, signature, StringComparison.Ordinal))
            {
                references = cached.References;
                return true;
            }
        }

        references = [];
        return false;
    }

    private void StoreCached(Guid chatId, string signature, List<ArtifactReference> references)
    {
        lock (_cacheSync)
            _cache[chatId] = new CachedChatScan(signature, references);
    }

    private void PruneCache(HashSet<Guid> liveChatIds)
    {
        lock (_cacheSync)
        {
            if (_cache.Count == liveChatIds.Count)
                return;

            foreach (var staleId in _cache.Keys.Where(id => !liveChatIds.Contains(id)).ToList())
                _cache.Remove(staleId);
        }
    }

    private sealed record CachedChatScan(string Signature, List<ArtifactReference> References);

    /// <summary>One occurrence of an artifact inside a specific chat message.</summary>
    internal sealed record ArtifactReference(
        string Key,
        string Location,
        string Name,
        string Extension,
        LibraryArtifactKind Kind,
        LibraryArtifactOrigin Origin,
        Guid ChatId,
        string ChatTitle,
        Guid? ProjectId,
        DateTimeOffset Timestamp,
        string? Description);

    private sealed class MergedReference
    {
        public MergedReference(ArtifactReference first)
        {
            Key = first.Key;
            IsLink = first.Kind == LibraryArtifactKind.Link;
            Newest = first;
            Oldest = first;
            ChatIds.Add(first.ChatId);
        }

        public string Key { get; }
        public bool IsLink { get; }
        public ArtifactReference Newest { get; private set; }
        public ArtifactReference Oldest { get; private set; }
        public HashSet<Guid> ChatIds { get; } = [];

        public void Absorb(ArtifactReference reference)
        {
            ChatIds.Add(reference.ChatId);

            if (reference.Timestamp > Newest.Timestamp)
                Newest = reference;

            if (reference.Timestamp < Oldest.Timestamp)
                Oldest = reference;
        }
    }
}
