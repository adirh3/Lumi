using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Models;

namespace Lumi.Services;

public sealed record UserPromptHistoryItem(
    Guid ChatId,
    Guid MessageId,
    string Content,
    DateTimeOffset Timestamp);

public partial class DataStore : IChatContentSearch
{
    internal const int SuggestionHistoryScanLimit = 1000;
    private static readonly TimeSpan SuggestionHistoryCacheTtl = TimeSpan.FromMinutes(2);
    private static readonly string AppDir = Path.Combine(
        ResolveAppDataRoot(), "Lumi");
    private static readonly string DataFile = Path.Combine(AppDir, "data.json");
    private static readonly string IndexLockFile = Path.Combine(AppDir, "data.lock");
    private static readonly TimeSpan StartupIndexLockTimeout = TimeSpan.FromSeconds(10);
    private static readonly string[] CodingLumiBrowserTools =
    [
        ToolDisplayHelper.BrowserOpenToolName,
        ToolDisplayHelper.BrowserTabsToolName,
        ToolDisplayHelper.BrowserScreenshotToolName,
        ToolDisplayHelper.BrowserLookToolName,
        ToolDisplayHelper.BrowserFindToolName,
        ToolDisplayHelper.BrowserDoToolName,
        ToolDisplayHelper.BrowserJsToolName
    ];

    public static string SkillsDir { get; } = Path.Combine(AppDir, "skills");
    public static string ChatsDir { get; } = Path.Combine(AppDir, "chats");
    public static string CopilotConfigDir { get; } = Path.Combine(AppDir, "copilot");
    internal static string AppDirectory => AppDir;

    /// <summary>
    /// True when <c>LUMI_APPDATA_DIR</c> gives this process its own profile (tests, isolated debug
    /// runs). Such a run leaves alone what belongs to the installed Lumi outside its profile, such
    /// as the launch-at-login registration.
    /// </summary>
    internal static bool UsesIsolatedProfile { get; } =
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LUMI_APPDATA_DIR"));

    /// <summary>Raised when a chat's persisted content changes (saved or deleted) so views of it can refresh.</summary>
    public event Action<Guid>? ChatContentChanged;

    /// <summary>Raised when all chats are cleared so views of them can refresh.</summary>
    public event Action? ChatsContentReset;

    /// <summary>
    /// Raised after <see cref="SaveAsync"/> completes, i.e. whenever the index (settings, projects,
    /// skills, agents, memories, MCP servers, jobs, chat tags, chat metadata) has been persisted.
    /// Every desktop CRUD path funnels through that save, so this is the single signal that "the
    /// library changed", no matter which ViewModel made the edit. May fire on any thread.
    /// </summary>
    public event Action? IndexSaved;

    /// <summary>
    /// Raised on the caller's thread after the shared chat-tag catalog changes so every open main
    /// window can refresh its independent tag manager.
    /// </summary>
    public event Action<object?>? ChatTagCatalogChanged;

    /// <summary>Tells every Settings window that shares this store that a look setting every window
    /// shares changed (the ambient presence, the accent colour or the window material), so each shows
    /// what is saved. Raised on the caller's thread; UI callers raise it on the UI thread.</summary>
    public event Action<object?>? AppearanceSettingsChanged;

    private AppData _data;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _skillSyncLock = new(1, 1);
    private readonly object _suggestionHistorySync = new();
    private IReadOnlyList<UserPromptHistoryItem>? _suggestionHistory;
    private DateTimeOffset _suggestionHistoryAt = DateTimeOffset.MinValue;
    private Task<IReadOnlyList<UserPromptHistoryItem>>? _suggestionHistoryRefresh;
    private int _suggestionHistoryGeneration;
    private readonly object _chatChangeSync = new();
    private readonly object _backgroundJobsSync = new();
    private readonly object _remoteSecuritySync = new();
    private readonly object _worktreeAssociationSync = new();
    private readonly HashSet<string> _worktreeCleanupReservations = new(
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
    private readonly Dictionary<Guid, long> _dirtyChatVersions = [];
    private readonly Dictionary<Guid, long> _deletedChatVersions = [];
    private readonly bool _usesPersistentStorage = true;
    private long _nextBackgroundJobsChangeVersion;
    private long _dirtyBackgroundJobsVersion;
    private long _nextRemotePairedDevicesChangeVersion;
    private long _dirtyRemotePairedDevicesVersion;
    private int? _activeSkillSyncHash;
    private string? _activeSkillSyncDirectory;
    private long _nextChatChangeVersion;

    /// <summary>
    /// Directory the skill markdown mirror and backups are written to. Defaults to the
    /// process-wide <see cref="SkillsDir"/>; tests can point it at an isolated folder.
    /// </summary>
    public string SkillsDirectory { get; }

    public DataStore()
    {
        SkillsDirectory = SkillsDir;
        Directory.CreateDirectory(AppDir);
        Directory.CreateDirectory(SkillsDir);
        Directory.CreateDirectory(CopilotConfigDir);
        _data = Load();
        // Chats live in the chat database. Chats the index still lists in the legacy layout are
        // imported first (metadata now, transcripts in the background), then the index drops them.
        var legacyIndexChats = _data.Chats;
        (_chatStore, var storedChats, _legacyImporter, _damagedChatsCopy) = OpenChatStorage(
            DatabaseFile, legacyIndexChats, ChatsDir, BackupLegacyIndexOnce);
        _chatStore.ChatContentCommitted += OnChatContentCommitted;
        _data.Chats = storedChats;
        if (legacyIndexChats.Count > 0)
            Save();
        // One-time migration: previous Lumi versions auto-injected an "api-key" header for Azure
        // AI Foundry URLs (services.ai.azure.com / *.openai.azure.com). Lumi no longer treats
        // those URLs specially — every BYOK endpoint is now a plain OpenAI-compatible target.
        // For users whose /responses path was working only because of that auto-injection,
        // promote the key into endpoint.Headers["api-key"] so existing setups keep working.
        // /chat/completions is also fixed (the bearer+api-key collision was breaking it).
        if (MigrateByokAutoInjectedApiKey(_data.Settings))
        {
            Save();
        }
        CleanOrphanedChats();
        SeedDefaults();
        SeedCodingLumi();
        EnsureCurrentChatManagementTool();
        if (MigrateCodingLumiBrowserTools(_data))
        {
            Save();
        }
        if (RemoveRetiredCodingHelpers(_data))
        {
            Save();
            SyncSkillFiles();
        }
        if (RemoveRetiredFeatureManagerSkill(_data))
        {
            Save();
            SyncSkillFiles();
        }

        StartLegacyImport();
        UpgradesChatStorage = _chatStore.NeedsUpgrade;
        _chatStorageUpkeep = _chatStore.StartMaintenance();
        ObserveFailure(_chatStorageUpkeep, "Chat database upkeep");
        RemoveRetiredSearchIndex();
    }

    internal DataStore(AppData data, string? skillsDirectoryOverride = null)
    {
        _usesPersistentStorage = false;
        _data = data ?? new AppData();
        SkillsDirectory = string.IsNullOrWhiteSpace(skillsDirectoryOverride) ? SkillsDir : skillsDirectoryOverride;
    }

    public AppData Data => _data;
    internal bool UsesPersistentStorage => _usesPersistentStorage;

    public void NotifyChatTagCatalogChanged(object? source = null)
        => ChatTagCatalogChanged?.Invoke(source);

    internal bool IsWorktreeCleanupReserved(string? path)
    {
        if (!TryNormalizeWorktreePath(path, out var normalized))
            return false;

        lock (_worktreeAssociationSync)
            return _worktreeCleanupReservations.Contains(normalized);
    }

    public void NotifyAppearanceSettingsChanged(object? source = null)
        => AppearanceSettingsChanged?.Invoke(source);

    internal bool TrySetChatWorktreePath(Chat chat, string? path)
    {
        ArgumentNullException.ThrowIfNull(chat);

        if (string.IsNullOrWhiteSpace(path))
        {
            lock (_worktreeAssociationSync)
            {
                chat.WorktreePath = null;
                return true;
            }
        }

        if (!TryNormalizeWorktreePath(path, out var normalized))
            return false;

        lock (_worktreeAssociationSync)
        {
            if (_worktreeCleanupReservations.Contains(normalized))
                return false;

            chat.WorktreePath = normalized;
            return true;
        }
    }

    internal WorktreeCleanupReservation? TryReserveWorktreeCleanup(
        Chat ownerChat,
        string path,
        out bool isShared)
    {
        ArgumentNullException.ThrowIfNull(ownerChat);
        isShared = false;
        if (!TryNormalizeWorktreePath(path, out var normalized))
            return null;

        lock (_worktreeAssociationSync)
        {
            if (_worktreeCleanupReservations.Contains(normalized))
                return null;

            isShared = _data.Chats.Any(chat =>
                chat.Id != ownerChat.Id &&
                WorktreePathsEqual(chat.WorktreePath, normalized));
            if (isShared)
                return null;

            _worktreeCleanupReservations.Add(normalized);
            var detachedOwnerPath = WorktreePathsEqual(ownerChat.WorktreePath, normalized)
                ? ownerChat.WorktreePath
                : null;
            if (detachedOwnerPath is not null)
                ownerChat.WorktreePath = null;

            return new WorktreeCleanupReservation(
                this,
                normalized,
                ownerChat,
                detachedOwnerPath);
        }
    }

    internal sealed class WorktreeCleanupReservation : IDisposable
    {
        private DataStore? _owner;
        private readonly string _normalizedPath;
        private readonly Chat _ownerChat;
        private readonly string? _detachedOwnerPath;

        internal WorktreeCleanupReservation(
            DataStore owner,
            string normalizedPath,
            Chat ownerChat,
            string? detachedOwnerPath)
        {
            _owner = owner;
            _normalizedPath = normalizedPath;
            _ownerChat = ownerChat;
            _detachedOwnerPath = detachedOwnerPath;
        }

        public bool RestoreOwnerAssociation()
        {
            var owner = _owner;
            if (owner is null || _detachedOwnerPath is null)
                return false;

            lock (owner._worktreeAssociationSync)
            {
                if (_ownerChat.WorktreePath is not null)
                    return false;

                _ownerChat.WorktreePath = _detachedOwnerPath;
                return true;
            }
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null)
                return;

            lock (owner._worktreeAssociationSync)
                owner._worktreeCleanupReservations.Remove(_normalizedPath);
        }
    }

    private static bool TryNormalizeWorktreePath(string? path, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            normalized = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return normalized.Length > 0;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool WorktreePathsEqual(string? left, string? right)
    {
        return TryNormalizeWorktreePath(left, out var normalizedLeft)
               && TryNormalizeWorktreePath(right, out var normalizedRight)
               && string.Equals(
                   normalizedLeft,
                   normalizedRight,
                   OperatingSystem.IsWindows()
                       ? StringComparison.OrdinalIgnoreCase
                       : StringComparison.Ordinal);
    }

    internal async Task RefreshRemoteSecurityFromDiskAsync(CancellationToken cancellationToken = default)
    {
        if (!_usesPersistentStorage)
            return;

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        FileStream? indexLock = null;
        try
        {
            indexLock = await AcquireIndexLockAsync(cancellationToken).ConfigureAwait(false);
            if (TryLoadIndexSnapshot() is not { } persisted)
                return;

            ApplyRemoteSecuritySnapshot(persisted.Settings);
        }
        finally
        {
            indexLock?.Dispose();
            _writeLock.Release();
        }
    }

    internal void ApplyRemoteSecuritySnapshot(UserSettings persisted)
    {
        lock (_remoteSecuritySync)
        {
            _data.Settings.RemoteAccessEnabled = persisted.RemoteAccessEnabled;
            _data.Settings.RemoteAccessPort = persisted.RemoteAccessPort;
            _data.Settings.RemoteAllowInsecureLan = persisted.RemoteAllowInsecureLan;
            _data.Settings.RemoteUseDevTunnel = persisted.RemoteUseDevTunnel;
            _data.Settings.RemoteDevTunnelId = persisted.RemoteDevTunnelId;
            _data.Settings.RemotePairedDevices = persisted.RemotePairedDevices
                .Select(CloneRemotePairedDevice)
                .ToList();
        }
    }

    private static string ResolveAppDataRoot()
    {
        var overrideRoot = Environment.GetEnvironmentVariable("LUMI_APPDATA_DIR");
        if (!string.IsNullOrWhiteSpace(overrideRoot))
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(overrideRoot));

        return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    }

    /// <summary>Marks a chat as needing persistence. Does not change <see cref="Chat.UpdatedAt"/>
    /// — callers that represent real user/assistant activity should bump the timestamp themselves.</summary>
    public void MarkChatChanged(Chat chat)
    {
        ArgumentNullException.ThrowIfNull(chat);

        var version = Interlocked.Increment(ref _nextChatChangeVersion);
        lock (_chatChangeSync)
        {
            _deletedChatVersions.Remove(chat.Id);
            _dirtyChatVersions[chat.Id] = version;
        }
    }

    public void MarkChatDeleted(Guid chatId)
    {
        var version = Interlocked.Increment(ref _nextChatChangeVersion);
        lock (_chatChangeSync)
        {
            _dirtyChatVersions.Remove(chatId);
            _deletedChatVersions[chatId] = version;
        }
    }

    public void MarkBackgroundJobsChanged()
    {
        var version = Interlocked.Increment(ref _nextBackgroundJobsChangeVersion);
        Interlocked.Exchange(ref _dirtyBackgroundJobsVersion, version);
    }

    public void MarkRemoteSecurityChanged()
    {
        var version = Interlocked.Increment(ref _nextRemotePairedDevicesChangeVersion);
        Interlocked.Exchange(ref _dirtyRemotePairedDevicesVersion, version);
    }

    public void MarkRemotePairedDevicesChanged() => MarkRemoteSecurityChanged();

    public IReadOnlyList<RemotePairedDevice> SnapshotRemotePairedDevices()
    {
        lock (_remoteSecuritySync)
            return _data.Settings.RemotePairedDevices.Select(CloneRemotePairedDevice).ToList();
    }

    public void UpsertRemotePairedDevice(RemotePairedDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        lock (_remoteSecuritySync)
        {
            var devices = _data.Settings.RemotePairedDevices;
            var existing = devices.FirstOrDefault(candidate =>
                string.Equals(candidate.DeviceId, device.DeviceId, StringComparison.Ordinal));
            if (existing is null)
            {
                devices.Add(CloneRemotePairedDevice(device));
            }
            else
            {
                existing.DeviceName = device.DeviceName;
                existing.Token = device.Token;
                existing.PairedAt = device.PairedAt;
                existing.LastSeenAt = device.LastSeenAt;
            }
        }

        MarkRemotePairedDevicesChanged();
    }

    public bool RemoveRemotePairedDevice(string deviceId)
    {
        var removed = false;
        lock (_remoteSecuritySync)
        {
            removed = _data.Settings.RemotePairedDevices.RemoveAll(candidate =>
                string.Equals(candidate.DeviceId, deviceId, StringComparison.Ordinal)) > 0;
        }

        if (removed)
            MarkRemotePairedDevicesChanged();
        return removed;
    }

    public void TouchRemotePairedDevice(string deviceId, DateTimeOffset lastSeenAt)
    {
        lock (_remoteSecuritySync)
        {
            if (_data.Settings.RemotePairedDevices.FirstOrDefault(candidate =>
                    string.Equals(candidate.DeviceId, deviceId, StringComparison.Ordinal)) is { } device)
            {
                device.LastSeenAt = lastSeenAt;
            }
        }
    }

    internal AppData CreateIndexSnapshot()
    {
        lock (_backgroundJobsSync)
        {
            lock (_remoteSecuritySync)
                return AppDataSnapshotFactory.CreateIndexSnapshot(_data, includeChats: _chatStore is null);
        }
    }

    public List<BackgroundJob> SnapshotBackgroundJobs()
    {
        lock (_backgroundJobsSync)
        {
            return _data.BackgroundJobs.ToList();
        }
    }

    public void AddBackgroundJob(BackgroundJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        lock (_backgroundJobsSync)
        {
            _data.BackgroundJobs.Add(job);
        }

        MarkBackgroundJobsChanged();
    }

    public bool RemoveBackgroundJob(BackgroundJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        bool removed;
        lock (_backgroundJobsSync)
        {
            removed = _data.BackgroundJobs.Remove(job);
        }

        if (removed)
            MarkBackgroundJobsChanged();
        return removed;
    }

    public int RemoveBackgroundJobsForChat(Guid chatId)
    {
        int removed;
        lock (_backgroundJobsSync)
        {
            removed = _data.BackgroundJobs.RemoveAll(job => job.ChatId == chatId || job.SourceChatId == chatId);
        }

        if (removed > 0)
            MarkBackgroundJobsChanged();
        return removed;
    }

    /// <summary>
    /// Saves the index file (settings, chat tags, projects, skills, agents, memories, jobs) and the
    /// metadata rows of changed chats. Does NOT save chat messages — use SaveChatAsync() for that.
    /// </summary>
    public void Save()
    {
        if (!_usesPersistentStorage)
            return;

        Task.Run(() => SaveAsync()).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Saves the index file (settings, chat tags, projects, skills, agents, memories, jobs) and the
    /// metadata rows of changed chats. Does NOT save chat messages — use SaveChatAsync() for that.
    /// </summary>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (!_usesPersistentStorage)
        {
            // In-memory stores still mutate the same AppData, so subscribers must hear about it.
            IndexSaved?.Invoke();
            return;
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        FileStream? indexLock = null;
        Dictionary<Guid, long>? dirtyChatVersions = null;
        Dictionary<Guid, long>? deletedChatVersions = null;
        long backgroundJobsChangeVersion = 0;
        long remotePairedDevicesChangeVersion = 0;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            indexLock = await AcquireIndexLockAsync(cancellationToken).ConfigureAwait(false);
            (dirtyChatVersions, deletedChatVersions) = SnapshotChatChangeVersions();
            backgroundJobsChangeVersion = SnapshotBackgroundJobsChangeVersion();
            remotePairedDevicesChangeVersion = SnapshotRemotePairedDevicesChangeVersion();
            var snapshot = CreateIndexSnapshot();
            var persistedSnapshot = TryLoadIndexSnapshot();
            if (persistedSnapshot is not null)
            {
                snapshot = AppDataSnapshotFactory.MergeChatIndexChanges(
                    snapshot,
                    persistedSnapshot,
                    new HashSet<Guid>(dirtyChatVersions.Keys),
                    new HashSet<Guid>(deletedChatVersions.Keys),
                    backgroundJobsChangeVersion > 0,
                    remotePairedDevicesChangeVersion > 0);
            }

            // The chat database owns chats; an index written by an older Lumi may still list some.
            if (_chatStore is not null)
                snapshot.Chats = [];

            await JsonFilePersistence.SaveAppDataAsync(
                DataFile,
                snapshot,
                cancellationToken).ConfigureAwait(false);

            if (_chatStore is not null)
                await PersistChatRowsAsync(dirtyChatVersions.Keys, deletedChatVersions.Keys).ConfigureAwait(false);

            AcknowledgeChatChangeVersions(dirtyChatVersions, deletedChatVersions);
            AcknowledgeBackgroundJobsChangeVersion(backgroundJobsChangeVersion);
            AcknowledgeRemotePairedDevicesChangeVersion(remotePairedDevicesChangeVersion);
        }
        finally
        {
            indexLock?.Dispose();
            _writeLock.Release();
        }

        IndexSaved?.Invoke();
    }

    private static RemotePairedDevice CloneRemotePairedDevice(RemotePairedDevice device) =>
        new()
        {
            DeviceId = device.DeviceId,
            DeviceName = device.DeviceName,
            Token = device.Token,
            PairedAt = device.PairedAt,
            LastSeenAt = device.LastSeenAt
        };

    /// <summary>
    /// Writes all skills as markdown files in the skills directory for the Copilot SDK.
    /// </summary>
    public void SyncSkillFiles()
    {
        Directory.CreateDirectory(SkillsDirectory);

        // Remove old files that no longer correspond to a skill
        var existingFiles = Directory.GetFiles(SkillsDirectory, "*.md");
        var validFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var skill in _data.Skills)
        {
            var safeName = SanitizeFileName(skill.Name);
            var fileName = $"{safeName}.md";
            validFileNames.Add(fileName);

            var filePath = Path.Combine(SkillsDirectory, fileName);
            var content = BuildSkillMarkdown(skill);
            File.WriteAllText(filePath, content);
        }

        foreach (var file in existingFiles)
        {
            if (!validFileNames.Contains(Path.GetFileName(file)))
                File.Delete(file);
        }
    }

    /// <summary>Renders a skill's on-disk markdown (frontmatter + body), matching the mirror layout.</summary>
    public static string BuildSkillMarkdown(Skill skill, string? name = null, string? description = null)
    {
        return $"""
            ---
            name: {EncodeYamlScalar(name ?? skill.Name)}
            description: {EncodeYamlScalar(description ?? skill.Description)}
            ---

            {skill.Content}
            """;
    }

    /// <summary>Encodes a string as a JSON-compatible YAML double-quoted scalar. Skill name and
    /// description are always strings, so quoting every value avoids YAML implicit typing
    /// (for example <c>true</c>, <c>123</c>, or a date) without custom scalar heuristics.</summary>
    public static string EncodeYamlScalar(string? value)
        => JsonSerializer.Serialize(value ?? "", AppDataJsonContext.Default.String);

    /// <summary>Reverses <see cref="EncodeYamlScalar"/>. Plain (unquoted) scalars are returned as-is
    /// for backward compatibility with older mirror files.</summary>
    public static string DecodeYamlScalar(string raw)
    {
        if (raw.Length < 2 || raw[0] != '"' || raw[^1] != '"')
            return raw;

        return JsonSerializer.Deserialize(raw, AppDataJsonContext.Default.String)
               ?? throw new JsonException("Quoted skill frontmatter value must be a JSON string.");
    }

    /// <summary>Absolute path of a skill's markdown mirror file for the given skill name.</summary>
    public string GetSkillFilePath(string skillName)
        => Path.Combine(SkillsDirectory, $"{SanitizeFileName(skillName)}.md");

    /// <summary>True when another skill's name sanitizes to the same mirror filename, which would
    /// let one skill's <c>.md</c> mirror overwrite another's (and make <c>import</c> read the wrong file).</summary>
    public bool SkillFileNameConflicts(string skillName, Guid? excludeSkillId = null)
    {
        var target = SanitizeFileName(skillName);
        return _data.Skills.Any(existing =>
            existing.Id != excludeSkillId
            && string.Equals(SanitizeFileName(existing.Name), target, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Directory holding rolling per-skill content backups.</summary>
    public string SkillBackupsDirectory => Path.Combine(SkillsDirectory, ".backups");

    /// <summary>
    /// Folder for the files a skill added from a catalog brought with it (scripts, references,
    /// templates). Keyed by id so renaming the skill never orphans them.
    /// </summary>
    public string GetSkillBundleDirectory(Guid skillId)
        => Path.Combine(SkillsDirectory, ".files", skillId.ToString("N"));

    /// <summary>Removes a deleted skill's bundled files. Best effort: a locked file is left behind.</summary>
    public void DeleteSkillBundle(Guid skillId)
    {
        var folder = GetSkillBundleDirectory(skillId);
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private const int MaxSkillBackupsPerSkill = 10;

    /// <summary>
    /// Writes the prior skill body to a timestamped backup and prunes to the most recent
    /// <see cref="MaxSkillBackupsPerSkill"/> backups for that skill. Returns the backup path.
    /// </summary>
    public string BackupSkillContent(Guid skillId, string priorContent)
    {
        var backupsDir = SkillBackupsDirectory;
        Directory.CreateDirectory(backupsDir);

        var baseName = $"{skillId}-{DateTime.Now:yyyyMMdd-HHmmss}";
        var path = Path.Combine(backupsDir, baseName + ".md");
        var attempt = 0;
        while (File.Exists(path))
        {
            attempt++;
            path = Path.Combine(backupsDir, $"{baseName}-{attempt}.md");
        }
        WriteAllTextAtomic(path, priorContent);

        var existing = new DirectoryInfo(backupsDir)
            .GetFiles($"{skillId}-*.md")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ThenByDescending(f => f.Name, StringComparer.Ordinal)
            .ToList();
        foreach (var stale in existing.Skip(MaxSkillBackupsPerSkill))
        {
            try { stale.Delete(); }
            catch (IOException) { /* best-effort prune */ }
        }

        return path;
    }

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static void WriteAllTextAtomic(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = Path.Combine(
            Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tempPath, contents, Utf8NoBom);
        File.Move(tempPath, path, overwrite: true);
    }

    /// <summary>
    /// Writes specific skills (by ID) to the skills directory. Returns the directory path.
    /// </summary>
    public string SyncSkillFilesForIds(List<Guid> skillIds)
    {
        return SyncSkillFilesForIdsAsync(skillIds).GetAwaiter().GetResult();
    }

    public async Task<string> SyncSkillFilesForIdsAsync(List<Guid> skillIds, CancellationToken cancellationToken = default)
    {
        var dir = Path.Combine(AppDir, "active-skills");
        var skillIdSet = skillIds.Count > 0 ? skillIds.ToHashSet() : null;
        var skills = skillIdSet is { Count: > 0 }
            ? _data.Skills.Where(s => skillIdSet.Contains(s.Id)).OrderBy(s => s.Id).ToList()
            : _data.Skills.OrderBy(s => s.Id).ToList();
        var syncHash = BuildSkillSyncHash(skills);

        await _skillSyncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_activeSkillSyncHash == syncHash
                && string.Equals(_activeSkillSyncDirectory, dir, StringComparison.Ordinal)
                && Directory.Exists(dir))
            {
                return dir;
            }

            Directory.CreateDirectory(dir);

            foreach (var f in Directory.GetFiles(dir, "*.md"))
                File.Delete(f);

            foreach (var skill in skills)
            {
                var safeName = SanitizeFileName(skill.Name);
                var filePath = Path.Combine(dir, $"{safeName}.md");
                var content = BuildSkillMarkdown(skill);
                await File.WriteAllTextAsync(filePath, content, cancellationToken).ConfigureAwait(false);
            }

            _activeSkillSyncHash = syncHash;
            _activeSkillSyncDirectory = dir;
            return dir;
        }
        finally
        {
            _skillSyncLock.Release();
        }
    }

    private static int BuildSkillSyncHash(IReadOnlyList<Skill> skills)
    {
        var hash = new HashCode();
        hash.Add(skills.Count);
        foreach (var skill in skills)
        {
            hash.Add(skill.Id);
            hash.Add(skill.Name, StringComparer.Ordinal);
            hash.Add(skill.Description, StringComparer.Ordinal);
            hash.Add(skill.Content, StringComparer.Ordinal);
        }
        return hash.ToHashCode();
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(sanitized) ? "skill" : sanitized;
    }

    internal sealed record PlatformSkillText(
        string DocumentWord,
        string DocumentExcel,
        string DocumentPowerPoint,
        string WebsiteDescription,
        string WebsitePresentation,
        string WebsiteSaveStep,
        string WebsiteOpenStep,
        string WebsiteOpenRule);

    internal static PlatformSkillText CreatePlatformSkillText(bool isWindows)
    {
        if (isWindows)
        {
            return new PlatformSkillText(
                "Use PowerShell with COM automation or python-docx to create Word documents.",
                "Use PowerShell with COM automation or openpyxl to create spreadsheets.",
                "Use PowerShell with COM automation or python-pptx to create presentations.",
                "Creates beautiful interactive websites from chat content and opens them in Lumi's browser",
                "Transform any content from the conversation into a beautiful, modern, interactive single-page website and present it in Lumi's built-in browser.",
                """
                5. **Save the file** — Use the `create` tool to write the HTML file. Use this exact path format:
                   - Path: `C:\Users\<username>\Documents\lumi-website-<short-slug>.html`
                   - Use the user's Documents folder (resolve from `$env:USERPROFILE` if needed via a quick PowerShell call).
                   - Use a short descriptive slug (e.g., `lumi-website-tokyo-itinerary.html`).
                """,
                """
                6. **Open in Lumi's browser** — Use the `lumi_browser_open` tool to navigate to the local file URL:
                   - Convert the file path to a `file:///` URL: replace backslashes with forward slashes and prefix with `file:///`.
                   - Example: `C:\Users\John\Documents\lumi-website-tokyo.html` → `file:///C:/Users/John/Documents/lumi-website-tokyo.html`
                   - This opens the website inside Lumi's built-in browser panel so the user sees it immediately.
                """,
                "Never use `localhost` or start a web server. Just save an `.html` file and open it with the `lumi_browser_open` tool.");
        }

        return new PlatformSkillText(
            "Use `python-docx` to create Word documents.",
            "Use `openpyxl` to create spreadsheets.",
            "Use `python-pptx` to create presentations.",
            "Creates beautiful interactive websites from chat content and opens them in a browser",
            "Transform any content from the conversation into a beautiful, modern, interactive single-page website and present it in a browser.",
            """
            5. **Save the file** — Use the `create` tool to write the HTML file:
               - Save it into the user's Documents folder, resolving the real path for the current OS
                 (e.g. `~/Documents` on macOS/Linux, `%USERPROFILE%\Documents` on Windows). Do NOT hardcode a drive letter.
               - Use a short descriptive slug (e.g., `lumi-website-tokyo-itinerary.html`).
            """,
            """
            6. **Open it so the user sees it immediately** — Convert the saved path to a `file://` URL
               (forward slashes; prefix `file://`), then open it in the user's default browser
               (`open <url>` on macOS, `xdg-open <url>` on Linux).
            """,
            "Never use `localhost` or start a web server. Just save an `.html` file and open it in a browser (see step 6 for the per-OS way to open it).");
    }

    internal static bool MigratePlatformSpecificBuiltInSkills(AppData data, bool isWindows)
    {
        var desired = CreatePlatformSkillText(isWindows);
        var obsolete = CreatePlatformSkillText(!isWindows);
        var changed = false;

        var documentCreator = FindBuiltInSkill(data, "Document Creator");
        changed |= ReplaceSkillContent(documentCreator, obsolete.DocumentWord, desired.DocumentWord);
        changed |= ReplaceSkillContent(documentCreator, obsolete.DocumentExcel, desired.DocumentExcel);
        changed |= ReplaceSkillContent(documentCreator, obsolete.DocumentPowerPoint, desired.DocumentPowerPoint);

        var websiteCreator = FindBuiltInSkill(data, "Website Creator");
        if (websiteCreator is not null
            && string.Equals(websiteCreator.Description, obsolete.WebsiteDescription, StringComparison.Ordinal))
        {
            websiteCreator.Description = desired.WebsiteDescription;
            changed = true;
        }

        changed |= ReplaceSkillContent(websiteCreator, obsolete.WebsitePresentation, desired.WebsitePresentation);
        changed |= ReplaceSkillContent(websiteCreator, obsolete.WebsiteSaveStep, desired.WebsiteSaveStep);
        changed |= ReplaceSkillContent(websiteCreator, obsolete.WebsiteOpenStep, desired.WebsiteOpenStep);
        changed |= ReplaceSkillContent(websiteCreator, obsolete.WebsiteOpenRule, desired.WebsiteOpenRule);
        return changed;
    }

    private static Skill? FindBuiltInSkill(AppData data, string name)
        => data.Skills.FirstOrDefault(skill =>
            skill.IsBuiltIn && string.Equals(skill.Name, name, StringComparison.OrdinalIgnoreCase));

    private static bool ReplaceSkillContent(Skill? skill, string obsolete, string desired)
    {
        if (skill is null || !skill.Content.Contains(obsolete, StringComparison.Ordinal))
            return false;

        skill.Content = skill.Content.Replace(obsolete, desired, StringComparison.Ordinal);
        return true;
    }

    private void SeedDefaults()
    {
        // A few built-in skills reference OS-specific mechanics (PowerShell COM automation, the
        // Windows-only embedded browser, Windows path formats). The Windows wording is preserved
        // verbatim so the Windows system prompt stays byte-for-byte unchanged; other platforms get
        // a portable equivalent that never advertises a Windows-only tool.
        var isWindows = OperatingSystem.IsWindows();
        var platformText = CreatePlatformSkillText(isWindows);
        if (_data.Settings.DefaultsSeeded)
        {
            if (MigratePlatformSpecificBuiltInSkills(_data, isWindows))
            {
                Save();
                SyncSkillFiles();
            }
            return;
        }

        // ── Default Skills ──

        _data.Skills.AddRange([
            new Skill
            {
                Name = "Document Creator",
                Description = "Creates Word, Excel, and PowerPoint documents from user descriptions",
                IconGlyph = "📄",
                IsBuiltIn = true,
                Content = $"""
                    # Document Creator

                    You can create Office documents for the user. When asked to create a document:

                    1. **Word (.docx)**: {platformText.DocumentWord}
                       Write the content with proper headings, formatting, and structure.
                    2. **Excel (.xlsx)**: {platformText.DocumentExcel}
                       Include headers, data formatting, and formulas where appropriate.
                    3. **PowerPoint (.pptx)**: {platformText.DocumentPowerPoint}
                       Create slides with titles, content, and professional layout.

                    Always save files to the user's working directory and report the file path.
                    Ask the user what content they want if not specified.
                    """
            },
            new Skill
            {
                Name = "Web Researcher",
                Description = "Searches the web and summarizes findings on any topic",
                IconGlyph = "🔍",
                IsBuiltIn = true,
                Content = """
                    # Web Researcher

                    When the user asks you to research a topic:

                    1. Search the web for relevant, up-to-date information
                    2. Visit the most promising results to gather details
                    3. Synthesize findings into a clear, well-organized summary
                    4. Include key facts, different perspectives, and source references
                    5. Highlight anything the user should be aware of

                    Present research in a readable format with sections and bullet points.
                    If the topic is broad, ask clarifying questions first.
                    """
            },
            new Skill
            {
                Name = "File Organizer",
                Description = "Helps organize, rename, and manage files and folders",
                IconGlyph = "📁",
                IsBuiltIn = true,
                Content = """
                    # File Organizer

                    Help the user organize their files and folders:

                    1. **Analyze**: List and categorize files in specified directories
                    2. **Organize**: Move files into logical folder structures (by type, date, project, etc.)
                    3. **Rename**: Batch rename files using patterns the user specifies
                    4. **Clean up**: Find duplicates, empty folders, or temporary files
                    5. **Summary**: Provide a report of what was organized

                    Always confirm with the user before moving or deleting files.
                    Create a backup plan for destructive operations.
                    """
            },
            new Skill
            {
                Name = "Code Helper",
                Description = "Writes, explains, and debugs code in any language",
                IconGlyph = "💻",
                IsBuiltIn = true,
                Content = """
                    # Code Helper

                    Assist the user with programming tasks:

                    1. **Write code**: Create scripts, applications, or utilities in any language
                    2. **Explain code**: Break down existing code into understandable explanations
                    3. **Debug**: Help find and fix issues in code
                    4. **Refactor**: Improve code structure and readability
                    5. **Convert**: Translate code between languages

                    Write clean, well-commented code. Save files with appropriate extensions.
                    Test scripts when possible by running them.
                    """
            },
            new Skill
            {
                Name = "Email Drafter",
                Description = "Composes professional emails based on context and tone",
                IconGlyph = "✉️",
                IsBuiltIn = true,
                Content = """
                    # Email Drafter

                    Help the user compose emails:

                    1. Ask for the recipient, purpose, and desired tone if not provided
                    2. Draft a clear, professional email with proper greeting and sign-off
                    3. Match the tone: formal, casual, urgent, appreciative, etc.
                    4. Keep emails concise and action-oriented
                    5. Offer to revise based on feedback

                    Adapt writing style to the user's preferences as you learn them.
                    """
            },
            new Skill
            {
                Name = "Website Creator",
                Description = platformText.WebsiteDescription,
                IconGlyph = "🌐",
                IsBuiltIn = true,
                Content = $"""
                    # Website Creator

                    {platformText.WebsitePresentation}

                    ## When to Use
                    Use this skill whenever the user asks you to visualize, present, or turn conversation content into a website or webpage. This works for any content type: itineraries, reports, plans, guides, comparisons, portfolios, dashboards, timelines, recipes, study notes, or anything else.

                    ## How to Create the Website

                    1. **Gather the content** — Use the information already discussed in the conversation. If important details are missing, ask the user before proceeding.

                    2. **Generate a single self-contained HTML file** — The entire website must be in ONE `.html` file with all CSS and JavaScript inlined (no external dependencies except CDN links for fonts/icons). Use modern web standards:
                       - **Clean semantic HTML5** structure
                       - **Modern CSS** with gradients, shadows, smooth transitions, and animations
                       - **Responsive layout** that works at any window size
                       - **Dark/light theme** — detect system preference with `prefers-color-scheme` and include a toggle button
                       - **Smooth scrolling** and scroll-triggered reveal animations
                       - **Interactive elements** — collapsible sections, tabs, hover effects, modals, tooltips, or cards as appropriate for the content
                       - **Visual hierarchy** — use color, spacing, typography, and layout to make information scannable and beautiful
                       - **Icons** — use inline SVG icons or a CDN icon library (e.g., Lucide, Heroicons, or Font Awesome via CDN) to add visual polish
                       - **Images** — when the content involves places, items, or concepts that benefit from imagery, use placeholder images from Lorem Picsum (`https://picsum.photos/WIDTH/HEIGHT?random=N` where N is a unique number per image) or generate CSS gradient/pattern backgrounds as decorative visuals. Always provide meaningful alt text.

                    3. **Design principles**:
                       - Use a harmonious color palette (2-3 primary colors with neutrals)
                       - Typography: use Google Fonts via CDN for headings (e.g., Inter, Poppins, or Playfair Display) and system font stack for body
                       - Generous whitespace and padding
                       - Card-based layouts for grouped content
                       - Subtle micro-animations (fade-in, slide-up on scroll via IntersectionObserver)
                       - Professional, polished look — as if designed by a UI designer
                       - Ensure text contrast meets accessibility standards (WCAG AA)

                    4. **Content-specific patterns** — adapt the layout to the content type:
                       - **Itineraries/timelines**: Day-by-day cards or a vertical timeline with icons and images for each stop
                       - **Reports/analysis**: Dashboard-style with stat cards, charts (use Chart.js from CDN if needed), and sections
                       - **Guides/how-tos**: Step-by-step layout with numbered sections, progress indicator, and collapsible details
                       - **Comparisons**: Side-by-side cards or a comparison table with highlighted differences
                       - **Lists/collections**: Filterable/searchable grid of cards with images and descriptions
                       - **Plans/projects**: Kanban-style or milestone timeline with status indicators
                       - **Recipes**: Ingredient sidebar + step-by-step instructions with timers
                       - **Profiles/portfolios**: Hero banner with bio, grid of work/projects
                       - **Study notes/knowledge**: Table of contents sidebar, collapsible sections, highlight boxes for key concepts

                    {platformText.WebsiteSaveStep}

                    {platformText.WebsiteOpenStep}

                    7. **Announce it** — Call `announce_file(filePath)` with the HTML file path so the user gets a clickable attachment. Then tell the user the website is ready and summarize what it contains.

                    ## Important Rules
                    - The HTML file MUST be fully self-contained and valid. All styles and scripts are inlined or loaded from CDNs.
                    - {platformText.WebsiteOpenRule}
                    - Make the website genuinely impressive — not a basic page with plain text. Use modern CSS, animations, and interactivity.
                    - If the conversation content is long, organize it into navigable sections with a sticky navigation bar or sidebar.
                    - Always include a header/hero section with a title and brief description.
                    - Include a small footer with "Created with Lumi ✦" branding.
                    - If any external CDN resources fail to load (e.g., Google Fonts), the page should still look good with fallback system fonts.
                    - Use `charset="UTF-8"` in the HTML head to support all languages and special characters.
                    """
            }
        ]);

        // ── Default Agents ──
        _data.Agents.AddRange([
            new LumiAgent
            {
                Name = "Daily Planner",
                Description = "Plans your day, manages tasks, and keeps you on track",
                IconGlyph = "📋",
                IsBuiltIn = true,
                SystemPrompt = """
                    You are the Daily Planner Lumi. Your purpose is to help the user plan and manage their day effectively.

                    Your responsibilities:
                    - Help create daily schedules and to-do lists
                    - Prioritize tasks using urgency and importance
                    - Set time blocks for focused work
                    - Suggest breaks and balance
                    - Review what was accomplished at end of day
                    - Track recurring tasks and habits

                    Be encouraging but realistic about time estimates. Help the user avoid overcommitting.
                    Use the current time of day to provide contextually relevant suggestions.
                    """
            },
            new LumiAgent
            {
                Name = "Creative Writer",
                Description = "Helps with writing, storytelling, and creative content",
                IconGlyph = "✍️",
                IsBuiltIn = true,
                SystemPrompt = """
                    You are the Creative Writer Lumi. You help the user with all forms of creative writing.

                    Your capabilities:
                    - Write stories, poems, essays, and articles
                    - Help brainstorm ideas and overcome writer's block
                    - Edit and improve existing writing
                    - Adapt to different styles and genres
                    - Create outlines and story structures
                    - Generate dialogue and character descriptions

                    Be imaginative and expressive. Match the user's creative vision.
                    Offer constructive suggestions without being prescriptive.
                    """
            },
            new LumiAgent
            {
                Name = "Learning Lumi",
                Description = "Learns about you from your computer to create personalized skills and agents",
                IconGlyph = "🧠",
                IsBuiltIn = true,
                IsLearningAgent = true,
                SystemPrompt = """
                    You are Learning Lumi, a specialized agent that learns about the user to create personalized experiences.

                    Your mission is to understand the user by exploring their computer (with permission) and conversations:

                    ## What to Learn
                    - **Work patterns**: What software they use, what files they work with, their profession
                    - **Interests**: Topics they research, content they create, hobbies
                    - **Workflows**: Repetitive tasks they do that could become skills
                    - **Communication style**: How they write, preferred tone, language patterns
                    - **Tools & preferences**: Editors, browsers, apps they rely on

                    ## How to Learn
                    1. Ask the user what you can explore (documents folder, desktop, recent files, etc.)
                    2. Look at file types, folder structures, and recently modified files
                    3. Note patterns in their work (e.g., "they work with spreadsheets a lot")
                    4. Suggest new Skills or Agents based on what you discover

                    ## Creating Skills & Agents
                    When you identify a pattern or need, propose creating:
                    - **Skills**: For specific tasks (e.g., "Budget Tracker" if they use many spreadsheets)
                    - **Agents**: For workflow areas (e.g., "Project Manager" if they manage multiple projects)

                    Format your proposals clearly:
                    ```
                    🆕 Proposed Skill: [Name]
                    Description: [What it does]
                    Why: [What you observed that suggests this would help]
                    ```

                    Always ask permission before exploring files. Be transparent about what you find.
                    Never read sensitive files (passwords, keys, financial data) - skip them.
                    Focus on patterns and metadata, not private content.
                    """
            }
        ]);

        _data.Settings.DefaultsSeeded = true;
        Save();
        SyncSkillFiles();
    }

    private void SeedCodingLumi()
    {
        if (_data.Settings.CodingLumiSeeded) return;

        // ── Coding Skills ──
        var architectureSkill = new Skill
        {
            Name = "Architecture Advisor",
            Description = "Expert software architecture guidance: system design, patterns, scalability, and trade-off analysis",
            IconGlyph = "🏛️",
            IsBuiltIn = true,
            Content = """
                # Architecture Advisor

                You are a senior software architect advisor. When the user discusses system design or architecture:

                ## Design Analysis
                1. **Understand requirements** — Ask about scale, team size, deployment targets, and constraints before recommending architecture
                2. **Evaluate trade-offs** — Every architectural decision has trade-offs. Present options with pros/cons, not just one "right" answer
                3. **Consider non-functional requirements** — Performance, scalability, reliability, maintainability, security, observability

                ## Common Patterns to Recommend
                - **Layered Architecture** — When clear separation of concerns is needed
                - **Microservices** — When independent deployment and scaling of components is required (warn about complexity)
                - **Event-Driven** — When components need loose coupling and async communication
                - **CQRS/Event Sourcing** — When read and write patterns differ significantly
                - **Clean Architecture** — When testability and dependency inversion are priorities
                - **Modular Monolith** — Often the best starting point before microservices

                ## Code Organization
                - Suggest project structure patterns appropriate for the tech stack
                - Recommend dependency injection and interface boundaries
                - Identify where abstractions help and where they add unnecessary complexity
                - Guide on module boundaries, API contracts, and data ownership

                ## Anti-Patterns to Flag
                - God classes/services, circular dependencies, leaky abstractions
                - Premature optimization, over-engineering, distributed monoliths
                - Missing error handling strategies, no observability plan

                ## Deliverables
                When asked to design a system:
                1. Start with a high-level diagram (use Mermaid)
                2. Identify key components and their responsibilities
                3. Define data flow and communication patterns
                4. Call out risks and mitigation strategies
                5. Suggest an implementation roadmap (what to build first)

                Always calibrate recommendations to the user's context — a solo developer's needs differ from a 50-person team.
                """
        };

        var debugSkill = new Skill
        {
            Name = "Debug Expert",
            Description = "Systematic debugging methodology: isolate, reproduce, diagnose, and fix bugs efficiently",
            IconGlyph = "🐛",
            IsBuiltIn = true,
            Content = """
                # Debug Expert

                You are a world-class debugger. When the user has a bug or issue, follow this systematic methodology:

                ## 1. Understand the Problem
                - Ask: What is the **expected** behavior vs **actual** behavior?
                - Ask: When did it start? What changed recently?
                - Ask: Is it reproducible? Under what conditions?
                - Ask: Any error messages, stack traces, or logs?

                ## 2. Reproduce the Bug
                - Create the simplest possible reproduction case
                - Identify the exact steps to trigger the issue
                - Note: intermittent bugs often indicate race conditions, state issues, or environmental factors

                ## 3. Isolate the Cause
                Use these techniques in order of efficiency:
                1. **Read the error** — Stack traces and error messages are the fastest clue
                2. **Binary search** — Comment out half the code, does the bug persist? Narrow down
                3. **Add logging** — Strategic print/log statements at boundaries
                4. **Check recent changes** — `git diff` and `git log` to find what changed
                5. **Check assumptions** — Verify inputs, state, environment variables, config
                6. **Rubber duck** — Explain the code flow step by step; the bug often reveals itself

                ## 4. Common Bug Categories
                - **Null reference** — Check all paths that could produce null
                - **Off-by-one** — Loop bounds, array indices, string slicing
                - **Race condition** — Shared state, async/await misuse, missing locks
                - **State corruption** — Unintended mutation, stale cache, missing reset
                - **Environment** — Different OS, version, config, permissions, locale
                - **Integration** — API contract mismatch, serialization issues, timeout

                ## 5. Fix and Verify
                - Fix the root cause, not the symptom
                - Add a test that fails before the fix and passes after
                - Check for similar bugs elsewhere in the codebase
                - Document what caused the bug for future reference

                ## Tools to Use
                - Run `git diff` to see recent changes
                - Read source files around the error location
                - Execute test commands to verify the fix
                - Review the surrounding code for related issues

                Never guess. Follow the evidence. The bug is always logical — code does exactly what it's told.
                """
        };

        var codeReviewSkill = new Skill
        {
            Name = "Code Reviewer Pro",
            Description = "Professional code review process: security, performance, design, and team standards",
            IconGlyph = "🔍",
            IsBuiltIn = true,
            Content = """
                # Code Reviewer Pro

                You are a senior engineer conducting a professional code review. Inspect the actual diff and surrounding repository code, and verify each finding.

                ## Review Process

                ### 1. Understand Context First
                - What does this code change accomplish?
                - Is this a new feature, bug fix, refactor, or optimization?
                - What's the scope of impact?

                ### 2. Inspect the Change
                Read the diff and relevant callers, dependencies, and tests. Check for:
                - Bug detection
                - Security vulnerability scanning
                - Performance issue identification
                - Code quality checks

                ### 3. Verify Findings
                Check findings against the change's intent and supported behavior:
                - **Design intent** — Does the approach make sense for the problem?
                - **Maintainability** — Will the next developer understand this in 6 months?
                - **Edge cases** — What inputs or states haven't been considered?
                - **Integration risk** — How does this change interact with the rest of the system?
                - **Missing tests** — Identify missing regression coverage and suggest focused tests
                - **Documentation** — Are public APIs and complex logic documented?

                ### 4. Feedback Style
                - Be specific: reference exact lines and show concrete alternatives
                - Prioritize: distinguish blocking issues from suggestions
                - Be constructive: explain WHY something is a problem, not just WHAT
                - Praise good patterns — reinforcement matters
                - Use these prefixes:
                  - 🔴 **Must fix** — Bugs, security issues, data loss risks
                  - 🟡 **Should fix** — Performance, maintainability, error handling gaps
                  - 🔵 **Consider** — Style, alternative approaches, nice-to-haves
                  - ✅ **Nice** — Good patterns worth calling out

                ### 5. Summary
                - Overall assessment: Approve / Request Changes / Needs Discussion
                - Top 3 action items
                - Confidence level in the change
                """
        };

        _data.Skills.AddRange([architectureSkill, debugSkill, codeReviewSkill]);

        // ── Coding Lumi Agent ──
        _data.Agents.Add(new LumiAgent
        {
            Name = "Coding Lumi",
            Description = "Elite coding agent: writes, reviews, debugs, tests, and architects software",
            IconGlyph = "⚡",
            IsBuiltIn = true,
            SkillIds = [architectureSkill.Id, debugSkill.Id, codeReviewSkill.Id],
            HasExplicitToolSelection = true,
            ToolNames = [
                "lumi_fetch",
                "announce_file", "skill", "recall_memory", "manage_current_chat",
                .. CodingLumiBrowserTools
            ],
            SystemPrompt = """
                You are **Coding Lumi** — an elite software engineering agent. You combine deep technical expertise with practical engineering wisdom to produce exceptional code and solve hard problems.

                ## Core Identity

                You think like a **senior staff engineer** at a top tech company. You:
                - Write code that is correct, readable, and maintainable — in that order
                - Understand that simplicity is the ultimate sophistication
                - Know when to follow patterns and when to break them with good reason
                - Consider the human who will read your code next

                ## Your Superpowers

                ### 🔨 Code Generation
                When writing code:
                - Produce **complete, runnable code** — never pseudocode unless explicitly asked
                - Follow the language's idioms and modern best practices
                - Handle errors properly at system boundaries
                - Use meaningful names that reveal intent
                - Keep functions focused — each should do one thing well
                - Prefer composition over inheritance
                - Write code that doesn't need comments; add comments only for *why*, never for *what*

                ### 🐛 Debugging
                When fixing bugs:
                - Read error messages and stack traces carefully — they usually tell you exactly what's wrong
                - Reproduce first, then diagnose
                - Use `git diff` and `git log` to understand recent changes
                - Fix the root cause, not the symptom
                - Verify the fix doesn't break anything else

                ### 🔍 Code Review
                Review the actual changes and surrounding code when:
                - The user asks for a code review
                - You want to validate code quality before delivering
                - You need to check for security vulnerabilities
                - You want a second opinion on code you've written

                ### 🧪 Test Generation
                Write focused tests using the project's existing framework when:
                - The user asks for tests
                - You've written new code that should be tested
                - You want to verify edge case coverage
                - You need to understand existing code through its test cases

                ### 📖 Code Explanation
                Read the implementation and its callers to explain code when:
                - The user needs to understand complex unfamiliar code
                - You need to analyze a large codebase section
                - Teaching-level explanation is needed

                ### 🏗️ Project Analysis
                Inspect the repository, project configuration, and dependencies when:
                - Working with a new codebase for the first time
                - The user asks about project structure or architecture
                - You need to understand the tech stack and conventions before making changes

                ## Engineering Principles

                1. **YAGNI** — Don't build what isn't needed yet
                2. **DRY** — But don't over-abstract. Duplication is cheaper than wrong abstraction
                3. **KISS** — The simplest solution that works is usually the best
                4. **Fail fast** — Validate inputs early, surface errors immediately
                5. **Least surprise** — Code should behave as the reader expects
                6. **Boy Scout Rule** — Leave code better than you found it (but don't goldplate)

                ## Workflow

                When given a coding task:
                1. **Understand** — Ask clarifying questions if the requirements are ambiguous
                2. **Plan** — Think through the approach before writing code. For complex tasks, outline the plan first
                3. **Implement** — Write clean, correct code following the project's conventions
                4. **Verify** — Run the code, check for errors, test edge cases
                5. **Review** — Check important changes against their callers, tests, and intended behavior
                6. **Deliver** — Present the solution clearly with any caveats or follow-up suggestions

                ## Language & Framework Expertise

                You are an expert in ALL major programming languages and frameworks. Adapt your style to match:
                - **C# / .NET** — Use modern C# (records, pattern matching, nullable refs, LINQ). Follow .NET conventions
                - **TypeScript / JavaScript** — Prefer TypeScript. Use modern ES features, proper async/await
                - **Python** — Use type hints, dataclasses, pathlib. Follow PEP 8
                - **Rust** — Embrace the borrow checker. Use Result<T,E> for error handling
                - **Go** — Keep it simple. Handle errors explicitly. Follow effective Go
                - **Java / Kotlin** — Prefer Kotlin when possible. Use modern Java features
                - Every other language — apply its community's idiomatic patterns

                ## Security Mindset

                Always consider:
                - Input validation at trust boundaries
                - SQL injection, XSS, command injection
                - Secret management (never hardcode credentials)
                - Principle of least privilege
                - Data sanitization and encoding

                ## Communication

                - Be direct and concise. Lead with code, explain after
                - When proposing multiple approaches, include trade-offs for each
                - If you're unsure about something, say so — then give your best reasoning
                - Use technical terminology accurately
                - When showing diffs/changes, explain what changed and why
                """
        });

        _data.Settings.CodingLumiSeeded = true;
        _data.Settings.CodingLumiBrowserToolsSeeded = true;
        Save();
        SyncSkillFiles();
    }

    internal static bool MigrateCodingLumiBrowserTools(AppData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Settings.CodingLumiBrowserToolsSeeded)
            return false;

        var codingLumi = data.Agents.FirstOrDefault(agent =>
            agent.IsBuiltIn
            && string.Equals(agent.Name, "Coding Lumi", StringComparison.OrdinalIgnoreCase));
        if (codingLumi is { HasToolRestrictions: true })
        {
            foreach (var toolName in CodingLumiBrowserTools)
            {
                if (!codingLumi.ToolNames.Contains(toolName, StringComparer.Ordinal))
                    codingLumi.ToolNames.Add(toolName);
            }
        }

        data.Settings.CodingLumiBrowserToolsSeeded = true;
        return true;
    }

    internal static bool RemoveRetiredCodingHelpers(AppData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var changed = data.Settings.AiFeatureModels.Remove("coding-helpers");
        foreach (var agent in data.Agents)
        {
            if (agent.ToolNames.RemoveAll(static name =>
                    name is "code_review" or "generate_tests" or "explain_code" or "analyze_project") > 0)
            {
                // An empty legacy allowlist must not become unrestricted after its last tool is removed.
                if (agent.ToolNames.Count == 0)
                    agent.HasExplicitToolSelection = true;
                changed = true;
            }

            if (!agent.IsBuiltIn || !agent.Name.Equals("Coding Lumi", StringComparison.OrdinalIgnoreCase))
                continue;

            var prompt = RetireCodingHelperInstructions(agent.SystemPrompt);
            if (prompt != agent.SystemPrompt)
            {
                agent.SystemPrompt = prompt;
                changed = true;
            }
        }

        foreach (var skill in data.Skills)
        {
            if (!skill.IsBuiltIn
                || !(skill.Name.Equals("Debug Expert", StringComparison.OrdinalIgnoreCase)
                     || skill.Name.Equals("Code Reviewer Pro", StringComparison.OrdinalIgnoreCase)))
                continue;

            var content = RetireCodingHelperInstructions(skill.Content);
            if (content != skill.Content)
            {
                skill.Content = content;
                changed = true;
            }
        }

        return changed;
    }

    // Replace only factory-authored wording; keep custom instructions, identities, and skill links.
    private static string RetireCodingHelperInstructions(string text) => text
        .Replace("- Use `code_review` tool to spot related issues",
            "- Review the surrounding code for related issues", StringComparison.Ordinal)
        .Replace("Use the `code_review` tool for automated analysis, then layer on your own expertise.",
            "Inspect the actual diff and surrounding repository code, and verify each finding.", StringComparison.Ordinal)
        .Replace("Use the `code_review` tool for an initial pass covering bugs, security, performance, and code quality. Treat every automated result as a hypothesis, not an instruction:",
            "Inspect the diff and surrounding code for bugs, security, performance, and code quality. Treat every finding as a hypothesis, not an instruction:", StringComparison.Ordinal)
        .Replace("### 2. Automated Analysis", "### 2. Inspect the Change", StringComparison.Ordinal)
        .Replace("Use the `code_review` tool to get an initial automated review of the code. This handles:",
            "Read the diff and relevant callers, dependencies, and tests. Check for:", StringComparison.Ordinal)
        .Replace("### 3. Human-Level Review (your added value)", "### 3. Verify Findings", StringComparison.Ordinal)
        .Replace("Go beyond what automated tools catch:",
            "Check findings against the change's intent and supported behavior:", StringComparison.Ordinal)
        .Replace("Use `generate_tests` to create test suggestions",
            "Identify missing regression coverage and suggest focused tests", StringComparison.Ordinal)
        .Replace("Use `generate_tests` only for a concrete gap in changed behavior;",
            "Add focused tests only for a concrete gap in changed behavior;", StringComparison.Ordinal)
        .Replace("You have a `code_review` tool that performs deep automated analysis. Use it when:",
            "Review the actual changes and surrounding code when:", StringComparison.Ordinal)
        .Replace("You have a `generate_tests` tool that creates comprehensive test suites. Use it when:",
            "Write focused tests using the project's existing framework when:", StringComparison.Ordinal)
        .Replace("You have an `explain_code` tool for deep analysis. Use it when:",
            "Read the implementation and its callers to explain code when:", StringComparison.Ordinal)
        .Replace("You have an `analyze_project` tool for project-level understanding. Use it when:",
            "Inspect the repository, project configuration, and dependencies when:", StringComparison.Ordinal)
        .Replace("Use `code_review` on your own output for important changes",
            "Check important changes against their callers, tests, and intended behavior", StringComparison.Ordinal);

    private void EnsureCurrentChatManagementTool()
    {
        if (_data.Settings.CurrentChatManagementToolSeeded)
            return;

        var codingLumi = _data.Agents.FirstOrDefault(agent =>
            agent.IsBuiltIn
            && string.Equals(agent.Name, "Coding Lumi", StringComparison.OrdinalIgnoreCase));
        if (codingLumi is { HasToolRestrictions: true }
            && !codingLumi.ToolNames.Contains("manage_current_chat", StringComparer.Ordinal))
        {
            codingLumi.ToolNames.Add("manage_current_chat");
        }

        _data.Settings.CurrentChatManagementToolSeeded = true;
        Save();
    }

    internal static bool RemoveRetiredFeatureManagerSkill(AppData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return data.Skills.RemoveAll(skill =>
            skill.IsBuiltIn
            && skill.Name.Equals("Lumi Feature Manager", StringComparison.OrdinalIgnoreCase)) > 0;
    }

    private static AppData Load()
    {
        using var timeout = new CancellationTokenSource(StartupIndexLockTimeout);
        try
        {
            using var indexLock = AcquireIndexLockAsync(timeout.Token)
                .GetAwaiter()
                .GetResult();
            return JsonFilePersistence.LoadAppData(DataFile);
        }
        catch (OperationCanceledException ex) when (timeout.IsCancellationRequested)
        {
            throw new IOException(
                $"Timed out waiting to read Lumi data after {StartupIndexLockTimeout.TotalSeconds:0} seconds.",
                ex);
        }
    }

    /// <summary>
    /// One-time migration that normalizes legacy Azure AI Foundry BYOK endpoints so custom
    /// <c>api-key</c> auth is dynamic and does not duplicate plaintext secrets in
    /// <see cref="ByokEndpoint.Headers"/>.
    ///
    /// Migration behavior:
    /// - If <c>headers["api-key"]</c> is missing and the endpoint has a resolvable BYOK key,
    ///   set it to <see cref="ByokConfigHelper.ApiKeyHeaderToken"/>.
    /// - If <c>headers["api-key"]</c> exists and equals the currently-resolved BYOK key,
    ///   replace it with <see cref="ByokConfigHelper.ApiKeyHeaderToken"/>.
    /// - If <c>headers["api-key"]</c> exists with a different user-specified value,
    ///   preserve it as-is.
    ///
    /// Safe to run on every startup and idempotent. Returns <c>true</c> when any endpoint was
    /// modified (caller should persist).
    /// </summary>
    internal static bool MigrateByokAutoInjectedApiKey(UserSettings settings)
    {
        var changed = false;
        foreach (var endpoint in settings.ByokEndpoints)
        {
            if (!ByokConfigHelper.IsAzureAiFoundryUrl(endpoint.BaseUrl))
                continue;

            if (endpoint.Headers is null)
            {
                endpoint.Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            else if (endpoint.Headers.Comparer != StringComparer.OrdinalIgnoreCase)
            {
                // Normalize to case-insensitive so subsequent lookups for "api-key" succeed.
                endpoint.Headers = new Dictionary<string, string>(endpoint.Headers, StringComparer.OrdinalIgnoreCase);
            }

            var key = ByokConfigHelper.ResolveApiKey(endpoint);

            if (endpoint.Headers.TryGetValue("api-key", out var existingValue))
            {
                // Already on dynamic placeholder format.
                if (string.Equals(existingValue, ByokConfigHelper.ApiKeyHeaderToken, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(existingValue, ByokConfigHelper.ApiKeyHeaderTokenLegacy, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Convert only when legacy literal matches the endpoint's resolved BYOK key.
                // If it differs, treat as explicit user override and preserve it.
                if (!string.IsNullOrEmpty(key)
                    && string.Equals(existingValue, key, StringComparison.Ordinal))
                {
                    endpoint.Headers["api-key"] = ByokConfigHelper.ApiKeyHeaderToken;
                    changed = true;
                }

                continue;
            }

            // No api-key header exists yet. If we can resolve the key, add dynamic placeholder
            // so the runtime value comes from ApiKeyMode (stored/env var) without persisting it
            // in plain text inside headers.
            if (!string.IsNullOrEmpty(key))
            {
                endpoint.Headers["api-key"] = ByokConfigHelper.ApiKeyHeaderToken;
                changed = true;
            }
        }
        return changed;
    }

    private (Dictionary<Guid, long> dirty, Dictionary<Guid, long> deleted) SnapshotChatChangeVersions()
    {
        lock (_chatChangeSync)
        {
            return (new(_dirtyChatVersions), new(_deletedChatVersions));
        }
    }

    private long SnapshotBackgroundJobsChangeVersion()
        => Volatile.Read(ref _dirtyBackgroundJobsVersion);

    private long SnapshotRemotePairedDevicesChangeVersion()
        => Volatile.Read(ref _dirtyRemotePairedDevicesVersion);

    private void AcknowledgeChatChangeVersions(
        IReadOnlyDictionary<Guid, long> dirtyChatVersions,
        IReadOnlyDictionary<Guid, long> deletedChatVersions)
    {
        lock (_chatChangeSync)
        {
            foreach (var (chatId, version) in dirtyChatVersions)
            {
                if (_dirtyChatVersions.TryGetValue(chatId, out var currentVersion)
                    && currentVersion == version)
                {
                    _dirtyChatVersions.Remove(chatId);
                }
            }

            foreach (var (chatId, version) in deletedChatVersions)
            {
                if (_deletedChatVersions.TryGetValue(chatId, out var currentVersion)
                    && currentVersion == version)
                {
                    _deletedChatVersions.Remove(chatId);
                }
            }
        }
    }

    private void AcknowledgeBackgroundJobsChangeVersion(long version)
    {
        if (version <= 0)
            return;

        Interlocked.CompareExchange(ref _dirtyBackgroundJobsVersion, 0, version);
    }

    private void AcknowledgeRemotePairedDevicesChangeVersion(long version)
    {
        if (version <= 0)
            return;

        Interlocked.CompareExchange(ref _dirtyRemotePairedDevicesVersion, 0, version);
    }

    private static AppData? TryLoadIndexSnapshot()
    {
        try
        {
            var primary = JsonFilePersistence.ReadPrimaryAppData(DataFile);
            return primary.Status == AppDataReadStatus.Valid ? primary.Data : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task<FileStream> AcquireIndexLockAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    IndexLockFile,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.None);
            }
            catch (IOException)
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }
    }

}
