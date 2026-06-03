using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot.SDK;
using Lumi.Models;

namespace Lumi.Services;

public sealed record LumiSharingSyncResult(
    int RepositoryCount,
    int SkillCount,
    int AgentCount,
    int McpServerCount,
    int MemoryCount);

public sealed record LumiSharingPublishResult(
    bool Success,
    string Message,
    string? RelativePath = null,
    string? BranchName = null,
    bool Pushed = false);

public sealed class LumiSharingService : IDisposable
{
    private static readonly StringComparer KeyComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly TimeSpan GitCommandTimeout = TimeSpan.FromSeconds(90);
    private readonly DataStore _dataStore;
    private readonly Func<DateTimeOffset> _nowProvider;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private CancellationTokenSource? _periodicSyncCts;

    public event Action? CapabilitiesChanged;
    public event Action? RepositoriesChanged;

    public LumiSharingService(DataStore dataStore, Func<DateTimeOffset>? nowProvider = null)
    {
        _dataStore = dataStore;
        _nowProvider = nowProvider ?? (() => DateTimeOffset.Now);
    }

    public void StartPeriodicSync()
    {
        if (_periodicSyncCts is not null)
            return;

        _periodicSyncCts = new CancellationTokenSource();
        _ = RunPeriodicSyncAsync(_periodicSyncCts.Token);
    }

    public async Task<LumiSharingSyncResult> SyncDueRepositoriesAsync(
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _nowProvider();
            var repositories = _dataStore.Data.SharedRepositories
                .Where(repository => repository.IsEnabled
                    && (force
                        || repository.NextSyncAt is null
                        || repository.NextSyncAt <= now))
                .ToList();

            var aggregate = new MutableSyncCounts();
            foreach (var repository in repositories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await SyncRepositoryCoreAsync(repository, cancellationToken).ConfigureAwait(false);
                aggregate.Add(result);
            }

            if (repositories.Count > 0)
            {
                await _dataStore.SaveAsync(cancellationToken).ConfigureAwait(false);
                _dataStore.SyncSkillFiles();
                CapabilitiesChanged?.Invoke();
                RepositoriesChanged?.Invoke();
            }

            return aggregate.ToResult(repositories.Count);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task<LumiSharingSyncResult> SyncRepositoryAsync(
        LumiSharedRepository repository,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);

        await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await SyncRepositoryCoreAsync(repository, cancellationToken).ConfigureAwait(false);
            await _dataStore.SaveAsync(cancellationToken).ConfigureAwait(false);
            _dataStore.SyncSkillFiles();
            CapabilitiesChanged?.Invoke();
            RepositoriesChanged?.Invoke();
            return result.ToResult(repositoryCount: 1);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task<LumiSharingPublishResult> PublishSkillAsync(
        LumiSharedRepository repository,
        Skill skill,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(skill);

        var repositoryPath = await EnsureRepositoryPathAsync(repository, cancellationToken).ConfigureAwait(false);
        var branchContext = await PreparePublishBranchAsync(repositoryPath, skill.Name, cancellationToken).ConfigureAwait(false);
        var relativePath = GetPublishPath(
            skill.SharedSource,
            repository,
            SharedCapabilityTypes.Skill,
            Path.Combine(".github", "skills", CreateSlug(skill.Name), "SKILL.md"));
        var filePath = Path.Combine(repositoryPath, ToLocalPath(relativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(
            filePath,
            BuildMarkdownAsset(skill.Name, skill.Description, skill.Content, skill.IconGlyph),
            cancellationToken).ConfigureAwait(false);

        var pushed = await CommitAndPushIfGitRepositoryAsync(
            repositoryPath,
            relativePath,
            branchContext,
            $"Share Lumi skill: {skill.Name}",
            cancellationToken).ConfigureAwait(false);
        var sourcePath = ToGitPath(relativePath);
        skill.SharedSource = CreateSource(repository, SharedCapabilityTypes.Skill, sourcePath, sourcePath, _nowProvider());
        return new LumiSharingPublishResult(
            true,
            BuildPublishMessage("skill", skill.Name, repository.DisplayName, branchContext?.BranchName, pushed),
            relativePath,
            branchContext?.BranchName,
            pushed);
    }

    public async Task<LumiSharingPublishResult> PublishAgentAsync(
        LumiSharedRepository repository,
        LumiAgent agent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(agent);

        var repositoryPath = await EnsureRepositoryPathAsync(repository, cancellationToken).ConfigureAwait(false);
        var branchContext = await PreparePublishBranchAsync(repositoryPath, agent.Name, cancellationToken).ConfigureAwait(false);
        var relativePath = GetPublishPath(
            agent.SharedSource,
            repository,
            SharedCapabilityTypes.Lumi,
            Path.Combine(".github", "agents", CreateSlug(agent.Name), "AGENT.md"));
        var filePath = Path.Combine(repositoryPath, ToLocalPath(relativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        var skillNames = agent.SkillIds
            .Select(id => _dataStore.Data.Skills.FirstOrDefault(skill => skill.Id == id)?.Name)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name!)
            .ToList();
        var mcpServerNames = agent.McpServerIds
            .Select(id => _dataStore.Data.McpServers.FirstOrDefault(server => server.Id == id)?.Name)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name!)
            .ToList();

        await File.WriteAllTextAsync(
            filePath,
            BuildMarkdownAsset(
                agent.Name,
                agent.Description,
                agent.SystemPrompt,
                agent.IconGlyph,
                skillNames,
                agent.ToolNames,
                mcpServerNames),
            cancellationToken).ConfigureAwait(false);

        var pushed = await CommitAndPushIfGitRepositoryAsync(
            repositoryPath,
            relativePath,
            branchContext,
            $"Share Lumi agent: {agent.Name}",
            cancellationToken).ConfigureAwait(false);
        var sourcePath = ToGitPath(relativePath);
        agent.SharedSource = CreateSource(repository, SharedCapabilityTypes.Lumi, sourcePath, sourcePath, _nowProvider());
        return new LumiSharingPublishResult(
            true,
            BuildPublishMessage("Lumi", agent.Name, repository.DisplayName, branchContext?.BranchName, pushed),
            relativePath,
            branchContext?.BranchName,
            pushed);
    }

    public async Task<LumiSharingPublishResult> PublishMemoryAsync(
        LumiSharedRepository repository,
        Memory memory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(memory);

        var repositoryPath = await EnsureRepositoryPathAsync(repository, cancellationToken).ConfigureAwait(false);
        var branchContext = await PreparePublishBranchAsync(repositoryPath, memory.Key, cancellationToken).ConfigureAwait(false);
        var relativePath = GetPublishPath(
            memory.SharedSource,
            repository,
            SharedCapabilityTypes.Memory,
            Path.Combine(".lumi", "memories.json"));
        var filePath = Path.Combine(repositoryPath, ToLocalPath(relativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        var entries = File.Exists(filePath)
            ? ReadMemoryEntries(filePath)
            : [];
        var existing = entries.FindIndex(entry => string.Equals(entry.Key, memory.Key, StringComparison.OrdinalIgnoreCase));
        var updated = new SharedMemoryEntry(
            memory.Key,
            memory.Content,
            memory.Category,
            memory.Scope,
            memory.Status,
            memory.Confidence);
        if (existing >= 0)
            entries[existing] = updated;
        else
            entries.Add(updated);

        await WriteMemoryEntriesAsync(filePath, entries, cancellationToken).ConfigureAwait(false);
        var pushed = await CommitAndPushIfGitRepositoryAsync(
            repositoryPath,
            relativePath,
            branchContext,
            $"Share Lumi memory: {memory.Key}",
            cancellationToken).ConfigureAwait(false);
        var sourcePath = ToGitPath(relativePath);
        memory.Source = "shared";
        memory.SharedSource = CreateSource(repository, SharedCapabilityTypes.Memory, $"{sourcePath}#{memory.Key}", sourcePath, _nowProvider());
        return new LumiSharingPublishResult(
            true,
            BuildPublishMessage("memory", memory.Key, repository.DisplayName, branchContext?.BranchName, pushed),
            relativePath,
            branchContext?.BranchName,
            pushed);
    }

    public async Task RemoveRepositoryAsync(
        LumiSharedRepository repository,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);

        await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RemoveStaleSkills(repository.Id, []);
            RemoveStaleAgents(repository.Id, []);
            RemoveStaleMcpServers(repository.Id, []);
            RemoveStaleMemories(repository.Id, []);
            _dataStore.Data.SharedRepositories.Remove(repository);
            await _dataStore.SaveAsync(cancellationToken).ConfigureAwait(false);
            _dataStore.SyncSkillFiles();
            CapabilitiesChanged?.Invoke();
            RepositoriesChanged?.Invoke();
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public void Dispose()
    {
        _periodicSyncCts?.Cancel();
        _periodicSyncCts?.Dispose();
        _periodicSyncCts = null;
        _syncLock.Dispose();
    }

    private async Task RunPeriodicSyncAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SyncDueRepositoriesAsync(force: false, cancellationToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                await SyncDueRepositoriesAsync(force: false, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[Sharing] Periodic sync stopped: {ex.Message}");
        }
    }

    private async Task<MutableSyncCounts> SyncRepositoryCoreAsync(
        LumiSharedRepository repository,
        CancellationToken cancellationToken)
    {
        var counts = new MutableSyncCounts();
        repository.IsSyncing = true;
        repository.LastSyncStatus = SharedRepositorySyncStatuses.Syncing;
        repository.LastSyncMessage = "Updating repository...";
        RepositoriesChanged?.Invoke();

        try
        {
            var repositoryPath = await EnsureRepositoryPathAsync(repository, cancellationToken).ConfigureAwait(false);
            var updateWarning = await PullIfGitRepositoryAsync(repositoryPath, cancellationToken).ConfigureAwait(false);

            var now = _nowProvider();
            counts.SkillCount = ImportSkills(repository, repositoryPath, now);
            counts.McpServerCount = ImportMcpServers(repository, repositoryPath, now);
            counts.MemoryCount = ImportMemories(repository, repositoryPath, now);
            counts.AgentCount = ImportAgents(repository, repositoryPath, now);

            repository.LastSkillCount = counts.SkillCount;
            repository.LastAgentCount = counts.AgentCount;
            repository.LastMcpServerCount = counts.McpServerCount;
            repository.LastMemoryCount = counts.MemoryCount;
            repository.LastSyncAt = now;
            repository.NextSyncAt = now.AddMinutes(Math.Max(5, repository.UpdateIntervalMinutes));
            repository.LastSyncStatus = SharedRepositorySyncStatuses.Synced;
            repository.LastSyncMessage = string.IsNullOrWhiteSpace(updateWarning)
                ? $"Synced {repository.CountsDisplay}."
                : $"Synced {repository.CountsDisplay}. Update warning: {updateWarning}";
            return counts;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            var now = _nowProvider();
            repository.LastSyncAt = now;
            repository.NextSyncAt = now.AddMinutes(Math.Max(5, repository.UpdateIntervalMinutes));
            repository.LastSyncStatus = SharedRepositorySyncStatuses.Error;
            repository.LastSyncMessage = ex.Message;
            return counts;
        }
        finally
        {
            repository.IsSyncing = false;
            RepositoriesChanged?.Invoke();
        }
    }

    private async Task<string> EnsureRepositoryPathAsync(
        LumiSharedRepository repository,
        CancellationToken cancellationToken)
    {
        var source = ExpandLocalPath(repository.Repository);
        if (!string.IsNullOrWhiteSpace(source) && Directory.Exists(source))
        {
            repository.LocalPath = source;
            return source;
        }

        var configuredLocalPath = ExpandLocalPath(repository.LocalPath);
        if (!string.IsNullOrWhiteSpace(configuredLocalPath) && Directory.Exists(configuredLocalPath))
        {
            repository.LocalPath = configuredLocalPath;
            return configuredLocalPath;
        }

        if (string.IsNullOrWhiteSpace(repository.Repository))
            throw new InvalidOperationException("Repository path or URL is required.");

        var clonePath = string.IsNullOrWhiteSpace(configuredLocalPath)
            ? Path.Combine(DataStore.SharedRepositoriesDir, repository.Id.ToString("N"))
            : configuredLocalPath;

        if (!Directory.Exists(clonePath))
        {
            Directory.CreateDirectory(DataStore.SharedRepositoriesDir);
            Directory.CreateDirectory(Path.GetDirectoryName(clonePath)!);
            var branchArgs = string.IsNullOrWhiteSpace(repository.Branch)
                ? ""
                : $" --branch {QuoteArgument(repository.Branch)}";
            var result = await RunGitAsync(
                DataStore.SharedRepositoriesDir,
                $"clone{branchArgs} {QuoteArgument(repository.Repository)} {QuoteArgument(clonePath)}",
                cancellationToken).ConfigureAwait(false);
            if (!result.Success)
                throw new InvalidOperationException($"Failed to clone repository: {result.Output}");
        }

        repository.LocalPath = clonePath;
        return clonePath;
    }

    private static async Task<string?> PullIfGitRepositoryAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        if (!GitService.IsGitRepo(repositoryPath))
            return null;

        var result = await RunGitAsync(repositoryPath, "pull --ff-only", cancellationToken).ConfigureAwait(false);
        return result.Success ? null : result.Output;
    }

    private static async Task<PublishBranchContext?> PreparePublishBranchAsync(
        string repositoryPath,
        string itemName,
        CancellationToken cancellationToken)
    {
        if (!GitService.IsGitRepo(repositoryPath))
            return null;

        var status = await RunGitAsync(repositoryPath, "status --porcelain", cancellationToken).ConfigureAwait(false);
        if (!status.Success)
            throw new InvalidOperationException($"Could not inspect repository before publishing: {status.Output}");
        if (!string.IsNullOrWhiteSpace(status.Output))
            throw new InvalidOperationException("The sharing repository has uncommitted changes. Sync or commit them before publishing another shared capability.");

        var originalBranchResult = await RunGitAsync(repositoryPath, "branch --show-current", cancellationToken).ConfigureAwait(false);
        var originalBranch = originalBranchResult.Success && !string.IsNullOrWhiteSpace(originalBranchResult.Output)
            ? originalBranchResult.Output.Trim()
            : null;
        var branchName = $"lumi/share/{CreateSlug(itemName)}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        var checkout = await RunGitAsync(
            repositoryPath,
            $"checkout -b {QuoteArgument(branchName)}",
            cancellationToken).ConfigureAwait(false);
        if (!checkout.Success)
            throw new InvalidOperationException($"Could not create sharing branch '{branchName}': {checkout.Output}");

        return new PublishBranchContext(branchName, originalBranch);
    }

    private static async Task<bool> CommitAndPushIfGitRepositoryAsync(
        string repositoryPath,
        string relativePath,
        PublishBranchContext? branchContext,
        string commitMessage,
        CancellationToken cancellationToken)
    {
        if (!GitService.IsGitRepo(repositoryPath))
            return false;

        try
        {
            var gitPath = ToGitPath(relativePath);
            var add = await RunGitAsync(
                repositoryPath,
                $"add -- {QuoteArgument(gitPath)}",
                cancellationToken).ConfigureAwait(false);
            if (!add.Success)
                throw new InvalidOperationException($"Published file but could not stage it in git: {add.Output}");

            var diff = await RunGitAsync(
                repositoryPath,
                "diff --cached --quiet",
                cancellationToken).ConfigureAwait(false);
            if (diff.Success)
                return false;

            var commit = await RunGitAsync(
                repositoryPath,
                $"-c user.name={QuoteArgument("Lumi")} -c user.email={QuoteArgument("lumi@local")} commit -m {QuoteArgument(commitMessage)}",
                cancellationToken).ConfigureAwait(false);
            if (!commit.Success)
                throw new InvalidOperationException($"Published file but could not commit it: {commit.Output}");

            var remote = await RunGitAsync(
                repositoryPath,
                "remote get-url origin",
                cancellationToken).ConfigureAwait(false);
            if (!remote.Success || string.IsNullOrWhiteSpace(branchContext?.BranchName))
                return false;

            var push = await RunGitAsync(
                repositoryPath,
                $"push -u origin {QuoteArgument(branchContext.BranchName)}",
                cancellationToken).ConfigureAwait(false);
            if (!push.Success)
                throw new InvalidOperationException($"Committed the shared capability on branch '{branchContext.BranchName}' but could not push it: {push.Output}");

            return true;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(branchContext?.OriginalBranch))
            {
                await RunGitAsync(
                    repositoryPath,
                    $"checkout {QuoteArgument(branchContext.OriginalBranch)}",
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private int ImportSkills(LumiSharedRepository repository, string repositoryPath, DateTimeOffset now)
    {
        var importedKeys = new HashSet<string>(KeyComparer);
        var definitions = DiscoverMarkdownAssets(
            repositoryPath,
            Path.Combine(".github", "skills"),
            "SKILL.md");

        foreach (var definition in definitions)
        {
            importedKeys.Add(definition.SourceKey);
            var existing = FindBySharedSource(
                _dataStore.Data.Skills,
                repository.Id,
                SharedCapabilityTypes.Skill,
                definition.SourceKey,
                static skill => skill.SharedSource);

            if (existing is null)
            {
                existing = new Skill { Id = StableGuid(repository.Id, SharedCapabilityTypes.Skill, definition.SourceKey) };
                _dataStore.Data.Skills.Add(existing);
            }

            existing.Name = definition.Name;
            existing.Description = definition.Description;
            existing.Content = definition.Content;
            existing.IconGlyph = string.IsNullOrWhiteSpace(definition.IconGlyph) ? "⚡" : definition.IconGlyph;
            existing.IsBuiltIn = false;
            existing.SharedSource = CreateSource(repository, SharedCapabilityTypes.Skill, definition.SourceKey, definition.RelativePath, now);
        }

        RemoveStaleSkills(repository.Id, importedKeys);
        return importedKeys.Count;
    }

    private int ImportAgents(LumiSharedRepository repository, string repositoryPath, DateTimeOffset now)
    {
        var importedKeys = new HashSet<string>(KeyComparer);
        var definitions = DiscoverMarkdownAssets(
            repositoryPath,
            Path.Combine(".github", "agents"),
            "AGENT.md");
        var skillIdsByName = _dataStore.Data.Skills
            .GroupBy(static skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First().Id, StringComparer.OrdinalIgnoreCase);
        var mcpIdsByName = _dataStore.Data.McpServers
            .GroupBy(static server => server.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First().Id, StringComparer.OrdinalIgnoreCase);

        foreach (var definition in definitions)
        {
            importedKeys.Add(definition.SourceKey);
            var existing = FindBySharedSource(
                _dataStore.Data.Agents,
                repository.Id,
                SharedCapabilityTypes.Lumi,
                definition.SourceKey,
                static agent => agent.SharedSource);

            if (existing is null)
            {
                existing = new LumiAgent { Id = StableGuid(repository.Id, SharedCapabilityTypes.Lumi, definition.SourceKey) };
                _dataStore.Data.Agents.Add(existing);
            }

            existing.Name = definition.Name;
            existing.Description = definition.Description;
            existing.SystemPrompt = definition.Content;
            existing.IconGlyph = string.IsNullOrWhiteSpace(definition.IconGlyph) ? "✦" : definition.IconGlyph;
            existing.IsBuiltIn = false;
            existing.IsLearningAgent = false;
            existing.SkillIds = definition.SkillNames
                .Where(skillIdsByName.ContainsKey)
                .Select(name => skillIdsByName[name])
                .Distinct()
                .ToList();
            existing.ToolNames = [..definition.ToolNames.Distinct(StringComparer.OrdinalIgnoreCase)];
            existing.McpServerIds = definition.McpServerNames
                .Where(mcpIdsByName.ContainsKey)
                .Select(name => mcpIdsByName[name])
                .Distinct()
                .ToList();
            existing.SharedSource = CreateSource(repository, SharedCapabilityTypes.Lumi, definition.SourceKey, definition.RelativePath, now);
        }

        RemoveStaleAgents(repository.Id, importedKeys);
        return importedKeys.Count;
    }

    private int ImportMcpServers(LumiSharedRepository repository, string repositoryPath, DateTimeOffset now)
    {
        var importedKeys = new HashSet<string>(KeyComparer);
        var catalog = ProjectContextCatalog.Discover(repositoryPath, project: null, copilotRootOverride: "");

        foreach (var definition in catalog.McpServers)
        {
            var relativePath = GetRelativePath(repositoryPath, definition.SourcePath);
            var sourceKey = $"{relativePath}#{definition.Name}";
            importedKeys.Add(sourceKey);
            var existing = FindBySharedSource(
                _dataStore.Data.McpServers,
                repository.Id,
                SharedCapabilityTypes.McpServer,
                sourceKey,
                static server => server.SharedSource);

            if (existing is null)
            {
                existing = new McpServer { Id = StableGuid(repository.Id, SharedCapabilityTypes.McpServer, sourceKey) };
                _dataStore.Data.McpServers.Add(existing);
            }

            ApplyMcpDefinition(existing, definition);
            existing.Description = $"Shared from {repository.DisplayName}";
            existing.IsEnabled = true;
            existing.SharedSource = CreateSource(repository, SharedCapabilityTypes.McpServer, sourceKey, relativePath, now);
        }

        RemoveStaleMcpServers(repository.Id, importedKeys);
        return importedKeys.Count;
    }

    private int ImportMemories(LumiSharedRepository repository, string repositoryPath, DateTimeOffset now)
    {
        var relativePath = Path.Combine(".lumi", "memories.json");
        var filePath = Path.Combine(repositoryPath, relativePath);
        var importedKeys = new HashSet<string>(KeyComparer);
        if (!File.Exists(filePath))
        {
            RemoveStaleMemories(repository.Id, importedKeys);
            return 0;
        }

        foreach (var entry in ReadMemoryEntries(filePath))
        {
            if (string.IsNullOrWhiteSpace(entry.Key) || string.IsNullOrWhiteSpace(entry.Content))
                continue;

            var sourceKey = $"{relativePath}#{entry.Key}";
            importedKeys.Add(sourceKey);
            var existing = FindBySharedSource(
                _dataStore.Data.Memories,
                repository.Id,
                SharedCapabilityTypes.Memory,
                sourceKey,
                static memory => memory.SharedSource);

            if (existing is null)
            {
                existing = new Memory
                {
                    Id = StableGuid(repository.Id, SharedCapabilityTypes.Memory, sourceKey),
                    CreatedAt = now
                };
                _dataStore.Data.Memories.Add(existing);
            }

            existing.Key = entry.Key;
            existing.Content = entry.Content;
            existing.Category = string.IsNullOrWhiteSpace(entry.Category) ? "Shared" : entry.Category;
            existing.Scope = string.IsNullOrWhiteSpace(entry.Scope) ? MemoryScopes.Global : entry.Scope;
            existing.Status = string.IsNullOrWhiteSpace(entry.Status) ? MemoryStatuses.Active : entry.Status;
            existing.Source = "shared";
            existing.UpdatedAt = now;
            existing.Confidence = entry.Confidence;
            existing.SharedSource = CreateSource(repository, SharedCapabilityTypes.Memory, sourceKey, relativePath, now);
        }

        RemoveStaleMemories(repository.Id, importedKeys);
        return importedKeys.Count;
    }

    private void RemoveStaleSkills(Guid repositoryId, HashSet<string> importedKeys)
    {
        var stale = _dataStore.Data.Skills
            .Where(skill => IsStaleSharedItem(skill.SharedSource, repositoryId, SharedCapabilityTypes.Skill, importedKeys))
            .ToList();
        foreach (var skill in stale)
        {
            foreach (var agent in _dataStore.Data.Agents)
                agent.SkillIds.Remove(skill.Id);
            foreach (var chat in _dataStore.Data.Chats)
            {
                if (chat.ActiveSkillIds.Remove(skill.Id))
                    _dataStore.MarkChatChanged(chat);
            }
            _dataStore.Data.Skills.Remove(skill);
        }
    }

    private void RemoveStaleAgents(Guid repositoryId, HashSet<string> importedKeys)
    {
        var stale = _dataStore.Data.Agents
            .Where(agent => IsStaleSharedItem(agent.SharedSource, repositoryId, SharedCapabilityTypes.Lumi, importedKeys))
            .ToList();
        foreach (var agent in stale)
        {
            foreach (var chat in _dataStore.Data.Chats.Where(chat => chat.AgentId == agent.Id))
            {
                chat.AgentId = null;
                _dataStore.MarkChatChanged(chat);
            }
            _dataStore.Data.Agents.Remove(agent);
        }
    }

    private void RemoveStaleMcpServers(Guid repositoryId, HashSet<string> importedKeys)
    {
        var stale = _dataStore.Data.McpServers
            .Where(server => IsStaleSharedItem(server.SharedSource, repositoryId, SharedCapabilityTypes.McpServer, importedKeys))
            .ToList();
        foreach (var server in stale)
        {
            foreach (var agent in _dataStore.Data.Agents)
                agent.McpServerIds.Remove(server.Id);
            foreach (var chat in _dataStore.Data.Chats)
            {
                if (chat.ActiveMcpServerNames.Remove(server.Name))
                    _dataStore.MarkChatChanged(chat);
            }
            _dataStore.Data.McpServers.Remove(server);
        }
    }

    private void RemoveStaleMemories(Guid repositoryId, HashSet<string> importedKeys)
    {
        _dataStore.Data.Memories.RemoveAll(memory =>
            IsStaleSharedItem(memory.SharedSource, repositoryId, SharedCapabilityTypes.Memory, importedKeys));
    }

    private static bool IsStaleSharedItem(
        SharedCapabilitySource? source,
        Guid repositoryId,
        string sourceType,
        HashSet<string> importedKeys)
        => source is not null
           && source.RepositoryId == repositoryId
           && string.Equals(source.SourceType, sourceType, StringComparison.OrdinalIgnoreCase)
           && !importedKeys.Contains(source.SourceKey);

    private static void ApplyMcpDefinition(McpServer server, ProjectContextMcpServerDefinition definition)
    {
        server.Name = definition.Name;
        server.Tools = ["*"];

        switch (definition.Config)
        {
            case McpStdioServerConfig stdio:
                server.ServerType = "local";
                server.Command = stdio.Command;
                server.Args = stdio.Args?.ToList() ?? [];
                server.Env = stdio.Env is null ? [] : new Dictionary<string, string>(stdio.Env);
                server.Url = "";
                server.Headers = [];
                break;
            case McpHttpServerConfig http:
                server.ServerType = "remote";
                server.Command = "";
                server.Args = [];
                server.Env = [];
                server.Url = http.Url;
                server.Headers = http.Headers is null ? [] : new Dictionary<string, string>(http.Headers);
                break;
        }
    }

    private static SharedCapabilitySource CreateSource(
        LumiSharedRepository repository,
        string type,
        string key,
        string path,
        DateTimeOffset now)
        => new()
        {
            RepositoryId = repository.Id,
            RepositoryName = repository.DisplayName,
            SourceType = type,
            SourceKey = key,
            SourcePath = path,
            LastSyncedAt = now
        };

    private static TItem? FindBySharedSource<TItem>(
        IEnumerable<TItem> items,
        Guid repositoryId,
        string sourceType,
        string sourceKey,
        Func<TItem, SharedCapabilitySource?> getSource)
        where TItem : class
        => items.FirstOrDefault(item =>
        {
            var source = getSource(item);
            return source is not null
                   && source.RepositoryId == repositoryId
                   && string.Equals(source.SourceType, sourceType, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(source.SourceKey, sourceKey, StringComparison.OrdinalIgnoreCase);
        });

    private static IReadOnlyList<SharedMarkdownAsset> DiscoverMarkdownAssets(
        string repositoryPath,
        string relativeDirectory,
        string nestedFileName)
    {
        var directory = Path.Combine(repositoryPath, relativeDirectory);
        if (!Directory.Exists(directory))
            return [];

        var assets = new List<SharedMarkdownAsset>();
        foreach (var file in Directory.GetFiles(directory, "*.md"))
        {
            var asset = ReadMarkdownAsset(repositoryPath, file, Path.GetFileNameWithoutExtension(file));
            if (asset is not null)
                assets.Add(asset);
        }

        foreach (var nestedDirectory in Directory.GetDirectories(directory))
        {
            var file = FindFile(nestedDirectory, nestedFileName);
            if (file is null)
                continue;

            var asset = ReadMarkdownAsset(repositoryPath, file, Path.GetFileName(nestedDirectory));
            if (asset is not null)
                assets.Add(asset);
        }

        return assets;
    }

    private static SharedMarkdownAsset? ReadMarkdownAsset(
        string repositoryPath,
        string filePath,
        string fallbackName)
    {
        var content = File.ReadAllText(filePath);
        var parsed = ParseMarkdownAsset(content, fallbackName);
        var relativePath = GetRelativePath(repositoryPath, filePath);
        return parsed with
        {
            RelativePath = relativePath,
            SourceKey = relativePath
        };
    }

    private static SharedMarkdownAsset ParseMarkdownAsset(string content, string fallbackName)
    {
        var normalized = content.Replace("\r\n", "\n");
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
            return new SharedMarkdownAsset(fallbackName, "", "", content.Trim(), [], [], [], "", "");

        var endOfFrontMatter = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (endOfFrontMatter < 0)
            return new SharedMarkdownAsset(fallbackName, "", "", content.Trim(), [], [], [], "", "");

        var frontMatterLines = normalized.Substring(4, endOfFrontMatter - 4).Split('\n');
        var body = normalized[(endOfFrontMatter + 5)..].Trim();
        var name = fallbackName;
        var description = "";
        var icon = "";
        var skills = new List<string>();
        var tools = new List<string>();
        var mcpServers = new List<string>();

        for (var i = 0; i < frontMatterLines.Length; i++)
        {
            var rawLine = frontMatterLines[i];
            if (string.IsNullOrWhiteSpace(rawLine) || char.IsWhiteSpace(rawLine[0]))
                continue;

            var colonIndex = rawLine.IndexOf(':');
            if (colonIndex <= 0)
                continue;

            var key = rawLine[..colonIndex].Trim();
            var value = rawLine[(colonIndex + 1)..].Trim();
            if (key.Equals("name", StringComparison.OrdinalIgnoreCase))
                name = ParseScalar(value) ?? fallbackName;
            else if (key.Equals("description", StringComparison.OrdinalIgnoreCase))
                description = ParseFrontMatterString(frontMatterLines, ref i, value);
            else if (key.Equals("icon", StringComparison.OrdinalIgnoreCase) || key.Equals("iconGlyph", StringComparison.OrdinalIgnoreCase))
                icon = ParseScalar(value) ?? "";
            else if (key.Equals("skills", StringComparison.OrdinalIgnoreCase))
                skills = ParseFrontMatterList(frontMatterLines, ref i, value);
            else if (key.Equals("tools", StringComparison.OrdinalIgnoreCase) || key.Equals("toolNames", StringComparison.OrdinalIgnoreCase))
                tools = ParseFrontMatterList(frontMatterLines, ref i, value);
            else if (key.Equals("mcpServers", StringComparison.OrdinalIgnoreCase))
                mcpServers = ParseFrontMatterList(frontMatterLines, ref i, value);
        }

        return new SharedMarkdownAsset(name, description, icon, body, skills, tools, mcpServers, "", "");
    }

    private static string ParseFrontMatterString(string[] lines, ref int index, string value)
    {
        if (value is ">" or ">-" or "|" or "|-")
        {
            var literal = value.StartsWith('|');
            var buffer = new List<string>();
            while (index + 1 < lines.Length && IsIndented(lines[index + 1]))
                buffer.Add(lines[++index].Trim());

            return literal
                ? string.Join(Environment.NewLine, buffer)
                : string.Join(" ", buffer.Where(static line => !string.IsNullOrWhiteSpace(line)));
        }

        return ParseScalar(value) ?? "";
    }

    private static List<string> ParseFrontMatterList(string[] lines, ref int index, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            return value.Trim().StartsWith('[')
                ? value.Trim().Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(static item => ParseScalar(item) ?? "")
                    .Where(static item => item.Length > 0)
                    .ToList()
                : [ParseScalar(value) ?? value];

        var result = new List<string>();
        while (index + 1 < lines.Length && IsIndented(lines[index + 1]))
        {
            var line = lines[++index].Trim();
            if (!line.StartsWith('-'))
                continue;

            var item = ParseScalar(line[1..].Trim());
            if (!string.IsNullOrWhiteSpace(item))
                result.Add(item);
        }

        return result;
    }

    private static bool IsIndented(string line)
        => !string.IsNullOrEmpty(line) && char.IsWhiteSpace(line[0]);

    private static string? ParseScalar(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        if ((trimmed.StartsWith('"') && trimmed.EndsWith('"'))
            || (trimmed.StartsWith('\'') && trimmed.EndsWith('\'')))
        {
            trimmed = trimmed[1..^1];
        }

        return trimmed.Replace("\\\"", "\"");
    }

    private static List<SharedMemoryEntry> ReadMemoryEntries(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        var array = root.ValueKind == JsonValueKind.Array
            ? root
            : root.ValueKind == JsonValueKind.Object
              && root.TryGetProperty("memories", out var memories)
              && memories.ValueKind == JsonValueKind.Array
                ? memories
                : default;

        if (array.ValueKind != JsonValueKind.Array)
            return [];

        var entries = new List<SharedMemoryEntry>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            entries.Add(new SharedMemoryEntry(
                GetOptionalString(item, "key") ?? "",
                GetOptionalString(item, "content") ?? "",
                GetOptionalString(item, "category") ?? "Shared",
                GetOptionalString(item, "scope") ?? MemoryScopes.Global,
                GetOptionalString(item, "status") ?? MemoryStatuses.Active,
                TryGetInt(item, "confidence")));
        }

        return entries;
    }

    private static async Task WriteMemoryEntriesAsync(
        string filePath,
        IReadOnlyList<SharedMemoryEntry> entries,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        await using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WritePropertyName("memories");
        writer.WriteStartArray();
        foreach (var entry in entries.OrderBy(static entry => entry.Category).ThenBy(static entry => entry.Key))
        {
            writer.WriteStartObject();
            writer.WriteString("key", entry.Key);
            writer.WriteString("content", entry.Content);
            writer.WriteString("category", entry.Category);
            writer.WriteString("scope", entry.Scope);
            writer.WriteString("status", entry.Status);
            if (entry.Confidence is { } confidence)
                writer.WriteNumber("confidence", confidence);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string BuildMarkdownAsset(
        string name,
        string description,
        string content,
        string icon,
        IReadOnlyList<string>? skills = null,
        IReadOnlyList<string>? tools = null,
        IReadOnlyList<string>? mcpServers = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine("---");
        builder.AppendLine($"name: {FormatYamlScalar(name)}");
        builder.AppendLine($"description: {FormatYamlScalar(description)}");
        if (!string.IsNullOrWhiteSpace(icon))
            builder.AppendLine($"icon: {FormatYamlScalar(icon)}");
        AppendYamlList(builder, "skills", skills);
        AppendYamlList(builder, "tools", tools);
        AppendYamlList(builder, "mcpServers", mcpServers);
        builder.AppendLine("---");
        builder.AppendLine();
        builder.AppendLine(content.Trim());
        return builder.ToString();
    }

    private static void AppendYamlList(StringBuilder builder, string key, IReadOnlyList<string>? values)
    {
        if (values is not { Count: > 0 })
            return;

        builder.AppendLine($"{key}:");
        foreach (var value in values.Where(static value => !string.IsNullOrWhiteSpace(value)))
            builder.AppendLine($"  - {FormatYamlScalar(value)}");
    }

    private static string FormatYamlScalar(string value)
        => $"\"{(value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    private static string CreateSlug(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWasDash = false;
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                previousWasDash = false;
            }
            else if (!previousWasDash)
            {
                builder.Append('-');
                previousWasDash = true;
            }
        }

        var slug = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "lumi-item" : slug;
    }

    private static Guid StableGuid(Guid repositoryId, string type, string sourceKey)
    {
        var bytes = Encoding.UTF8.GetBytes($"{repositoryId:N}:{type}:{sourceKey.ToLowerInvariant()}");
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return new Guid(hash[..16]);
    }

    private static string? FindFile(string parentDir, string name)
    {
        var exact = Path.Combine(parentDir, name);
        if (File.Exists(exact))
            return exact;

        return Directory.GetFiles(parentDir)
            .FirstOrDefault(file => string.Equals(Path.GetFileName(file), name, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetRelativePath(string root, string filePath)
        => ToGitPath(Path.GetRelativePath(root, filePath));

    private static string ToGitPath(string path)
        => path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

    private static string ToLocalPath(string path)
        => path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

    private static string GetPublishPath(
        SharedCapabilitySource? source,
        LumiSharedRepository repository,
        string sourceType,
        string fallbackPath)
    {
        if (source is not null
            && source.RepositoryId == repository.Id
            && string.Equals(source.SourceType, sourceType, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(source.SourcePath))
        {
            return ToGitPath(source.SourcePath);
        }

        return ToGitPath(fallbackPath);
    }

    private static string ExpandLocalPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || LooksLikeRemoteGitReference(value))
            return "";

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "";
        }
    }

    private static bool LooksLikeRemoteGitReference(string value)
        => value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
           || value.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)
           || value.Contains('@') && value.Contains(':');

    private static string? GetOptionalString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? TryGetInt(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : null;

    private static string QuoteArgument(string value)
        => $"\"{value.Replace("\"", "\\\"")}\"";

    private static string BuildPublishMessage(
        string itemType,
        string itemName,
        string repositoryName,
        string? branchName,
        bool pushed)
    {
        if (string.IsNullOrWhiteSpace(branchName))
            return $"Published {itemType} \"{itemName}\" to local repository folder {repositoryName}.";

        return pushed
            ? $"Published {itemType} \"{itemName}\" to {repositoryName} on branch {branchName}."
            : $"Published {itemType} \"{itemName}\" to {repositoryName} on local branch {branchName}. No origin remote was available to push.";
    }

    private static async Task<GitCommandResult> RunGitAsync(
        string workingDirectory,
        string arguments,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(GitCommandTimeout);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        process.StartInfo.Environment["GCM_INTERACTIVE"] = "Never";

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new GitCommandResult(false, ex.Message);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            return new GitCommandResult(false, $"git {arguments} timed out after {GitCommandTimeout.TotalSeconds:N0} seconds.");
        }

        var stdout = await outputTask.ConfigureAwait(false);
        var stderr = await errorTask.ConfigureAwait(false);
        var output = (stdout + stderr).Trim();
        return new GitCommandResult(process.ExitCode == 0, output);
    }

    private sealed record SharedMarkdownAsset(
        string Name,
        string Description,
        string IconGlyph,
        string Content,
        List<string> SkillNames,
        List<string> ToolNames,
        List<string> McpServerNames,
        string RelativePath,
        string SourceKey);

    private sealed record SharedMemoryEntry(
        string Key,
        string Content,
        string Category,
        string Scope,
        string Status,
        int? Confidence);

    private sealed record GitCommandResult(bool Success, string Output);

    private sealed record PublishBranchContext(string BranchName, string? OriginalBranch);

    private sealed class MutableSyncCounts
    {
        public int SkillCount { get; set; }
        public int AgentCount { get; set; }
        public int McpServerCount { get; set; }
        public int MemoryCount { get; set; }

        public void Add(MutableSyncCounts other)
        {
            SkillCount += other.SkillCount;
            AgentCount += other.AgentCount;
            McpServerCount += other.McpServerCount;
            MemoryCount += other.MemoryCount;
        }

        public LumiSharingSyncResult ToResult(int repositoryCount)
            => new(repositoryCount, SkillCount, AgentCount, McpServerCount, MemoryCount);
    }
}
