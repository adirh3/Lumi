using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GitHub.Copilot;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Capabilities;
using StrataTheme.Controls;

namespace Lumi.ViewModels;

/// <summary>
/// Skills, MCP servers, agents, projects, and attachment management.
/// </summary>
public partial class ChatViewModel
{
    /// <summary>Whether the agent can still be changed.</summary>
    public bool CanChangeAgent => true;

    public void SetActiveAgent(LumiAgent? agent)
    {
        var previousAgent = ActiveAgent;
        var changed = previousAgent?.Id != agent?.Id;
        ActiveAgent = agent;
        if (_suppressAgentSelectionSideEffects || IsEditingMessage)
            return;

        if (CurrentChat is not null)
        {
            CurrentChat.AgentId = agent?.Id;
            QueueSaveChat(CurrentChat, saveIndex: true);
        }

        if (changed)
            InvalidateAgentSession();
    }

    private async Task SelectAgentOnSessionAsync(string agentName)
    {
        if (_activeSession is null) return;
        try
        {
            await _activeSession.Rpc.Agent.SelectAsync(agentName);
        }
        catch { /* best effort */ }
    }

    private async Task DeselectAgentOnSessionAsync()
    {
        if (_activeSession is null) return;
        try
        {
            await _activeSession.Rpc.Agent.DeselectAsync();
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Applies an MCP selection change to the live session. Without this the picker only edits what
    /// the *next* session would be built with, so a server the user deselected mid-chat keeps
    /// offering its tools for the rest of the conversation.
    /// </summary>
    private async Task SetMcpEnabledOnSessionAsync(string serverName, bool enabled)
    {
        if (_activeSession is null || string.IsNullOrWhiteSpace(serverName)) return;

        // Lumi supplies its servers under a CAPI-safe namespace, so a display name like
        // "Avalonia MCP" is registered as "Avalonia_MCP". Calling with the display name would
        // silently target a server the session never registered, so use the plan this chat's own
        // session was built from.
        var runtimeKey = CurrentChat is { } chat && _sessionMcpPlans.TryGetValue(chat.Id, out var plan)
            ? plan.ResolveRuntimeKey(serverName)
            : serverName;

        try
        {
            if (enabled)
                await _activeSession.Rpc.Mcp.EnableAsync(runtimeKey);
            else
                await _activeSession.Rpc.Mcp.DisableAsync(runtimeKey);
        }
        catch { /* best effort — the next session is still built from the persisted selection */ }
    }

    /// <summary>Assigns a project to the current (or next) chat. Called when a project filter is active.</summary>
    public void SetProjectId(Guid projectId)
    {
        if (CurrentChat is not null)
        {
            if (IsExternalSendReserved(CurrentChat.Id))
            {
                SyncComposerProjectSelectionFromState();
                return;
            }

            var changed = CurrentChat.ProjectId != projectId;
            CurrentChat.ProjectId = projectId;
            QueueSaveChat(CurrentChat, saveIndex: true);
            if (changed)
                OnPropertyChanged(nameof(CurrentChat));

            // If project context changed on an existing chat, force a fresh Copilot session
            // so the next turn uses the updated project system prompt.
            if (changed && CurrentChat.CopilotSessionId is not null)
            {
                InvalidateCurrentSession();
                _pendingSkillInjections.Clear();
            }
        }
        else
        {
            // Will be applied when the chat is created in SendMessage
            _pendingProjectId = projectId;
            ApplyDraftProjectWorkspaceDefault(projectId);
            OnPropertyChanged(nameof(CurrentChat));
        }

        SyncComposerProjectSelectionFromState();
        RefreshProjectBadge();
        RefreshCapabilities();
        RefreshActiveSkillChipsFromState();
        QueueRefreshCodingProjectState();
    }

    private Guid? _pendingProjectId;

    /// <summary>
    /// Current project filter from the shell sidebar. Used as a fallback when creating a new chat
    /// to avoid losing project context due UI timing or unchanged filter selections.
    /// </summary>
    private Guid? _activeProjectFilterId;
    public Guid? ActiveProjectFilterId
    {
        get => _activeProjectFilterId;
        set
        {
            if (_activeProjectFilterId == value)
                return;

            _activeProjectFilterId = value;
            RefreshCapabilities();
            RefreshActiveSkillChipsFromState();
            QueueRefreshCodingProjectState();
        }
    }

    /// <summary>Removes the project assignment from the current chat.</summary>
    public void ClearProjectId()
    {
        if (CurrentChat is not null)
        {
            if (IsExternalSendReserved(CurrentChat.Id))
            {
                SyncComposerProjectSelectionFromState();
                return;
            }

            var changed = CurrentChat.ProjectId is not null;
            CurrentChat.ProjectId = null;
            QueueSaveChat(CurrentChat, saveIndex: true);
            if (changed)
                OnPropertyChanged(nameof(CurrentChat));

            if (changed && CurrentChat.CopilotSessionId is not null)
            {
                InvalidateCurrentSession();
                _pendingSkillInjections.Clear();
            }
        }
        else
        {
            _pendingProjectId = null;
            ApplyDraftProjectWorkspaceDefault(null);
            OnPropertyChanged(nameof(CurrentChat));
        }

        SyncComposerProjectSelectionFromState();
        RefreshProjectBadge();
        RefreshCapabilities();
        RefreshActiveSkillChipsFromState();
        QueueRefreshCodingProjectState();
    }

    internal bool IsExternalProjectContextCurrent(
        Chat targetChat,
        Guid? expectedProjectId,
        string? expectedProjectDirectory)
    {
        if (targetChat.ProjectId != expectedProjectId)
            return false;

        var currentDirectory = expectedProjectId is { } projectId
            ? _dataStore.Data.Projects
                .FirstOrDefault(project => project.Id == projectId)?
                .WorkingDirectory
            : null;
        return ProjectPathsEqual(currentDirectory, expectedProjectDirectory);
    }

    private static bool ProjectPathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right);

        var normalizedLeft = Path.GetFullPath(left)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRight = Path.GetFullPath(right)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(
            normalizedLeft,
            normalizedRight,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    private void ApplyDraftProjectWorkspaceDefault(Guid? projectId)
    {
        if (CurrentChat is not null)
            return;

        WorktreePath = null;
        var project = projectId.HasValue
            ? _dataStore.Data.Projects.FirstOrDefault(candidate => candidate.Id == projectId.Value)
            : null;
        IsWorktreeMode = project is
        {
            DefaultNewChatsUseWorktree: true,
            WorkingDirectory: { Length: > 0 } workingDirectory
        } && GitService.IsGitRepo(workingDirectory);
    }

    public void AddSkill(Skill skill)
    {
        if (ActiveSkillIds.Contains(skill.Id)) return;
        ActiveSkillIds.Add(skill.Id);
        ActiveSkillChips.Add(new StrataTheme.Controls.StrataComposerChip(skill.Name, skill.IconGlyph));
        // If added to an existing chat with a session, inject via next message instead of system prompt
        if (!IsEditingMessage && CurrentChat?.CopilotSessionId is not null)
            _pendingSkillInjections.Add(skill.Id);
        SyncActiveSkillsToChat();
    }

    /// <summary>File-based Copilot skill names currently selected for this chat.</summary>
    private readonly List<string> _activeExternalSkillNames = new();

    /// <summary>
    /// True once the user has curated the draft composer's MCP selection. A draft has no chat to
    /// record this on, so it is held here until the first send creates one.
    /// </summary>
    private bool _draftMcpSelectionCurated;

    /// <summary>
    /// The MCP plan each live session was built from, keyed by its chat. Used to translate a
    /// display name into the namespace that session registered it under. Switching chats adopts a
    /// cached session without rebuilding a plan, so this has to be keyed the same way the session
    /// cache is rather than tracking only the most recently built one.
    /// </summary>
    private readonly Dictionary<Guid, McpSessionPlan> _sessionMcpPlans = new();
    /// <summary>Proxy registrations owned by each locally attached Copilot session handle.</summary>
    private readonly Dictionary<CopilotSession, McpProxySessionLease> _mcpProxyLeasesBySession =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>Registers a skill selection without adding a chip (composer already added it).</summary>
    public void RegisterSkillIdByName(string name)
    {
        var skill = _dataStore.Data.Skills.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (skill is not null)
        {
            if (ActiveSkillIds.Contains(skill.Id)) return;
            ActiveSkillIds.Add(skill.Id);
            // If added to an existing chat with a session, inject via next message
            if (!IsEditingMessage && CurrentChat?.CopilotSessionId is not null)
                _pendingSkillInjections.Add(skill.Id);
            SyncActiveSkillsToChat();
            return;
        }

        var externalSkill = GetCapabilities().FindSkill(name);
        if (externalSkill is null
            || externalSkill.Origin.IsLumi
            || _activeExternalSkillNames.Any(existing =>
                existing.Equals(externalSkill.Name, StringComparison.OrdinalIgnoreCase)))
            return;

        _activeExternalSkillNames.Add(externalSkill.Name);
        SyncActiveSkillsToChat();
        // Unlike Lumi-managed skills there is no system-prompt path for file-based Copilot skills,
        // so queue unconditionally: the next send activates them through the SDK slash command.
        if (!IsEditingMessage)
            _pendingExternalSkillInjections.Add(externalSkill.Name);
    }

    private void SyncActiveSkillsToChat()
    {
        if (!IsEditingMessage && CurrentChat is not null)
        {
            CurrentChat.ActiveSkillIds = new List<Guid>(ActiveSkillIds);
            CurrentChat.ActiveExternalSkillNames = new List<string>(_activeExternalSkillNames);
            QueueSaveChat(CurrentChat, saveIndex: true);
        }
    }

    public void RemoveSkillByName(string name)
    {
        var skill = _dataStore.Data.Skills.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var changed = false;

        if (skill is not null)
            changed = ActiveSkillIds.Remove(skill.Id);
        else if (_activeExternalSkillNames.RemoveAll(existing => existing.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0)
            changed = true;

        if (!changed)
            return;

        var chip = ActiveSkillChips.OfType<StrataTheme.Controls.StrataComposerChip>()
            .FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (chip is not null)
            ActiveSkillChips.Remove(chip);

        SyncActiveSkillsToChat();
        // Deselecting has to retract the skill's queued one-shot delivery, not just the selection.
        // Both queues hold work for the NEXT send only: a Lumi-managed id waiting to be inlined into
        // the prompt, or a file-based name waiting for SDK activation. Leaving an entry behind would
        // apply a skill the user just removed. Restoring the pending-subset-of-active invariant here
        // covers both queues from one place, so the two skill systems cannot drift apart again.
        PrunePendingSkillInjections();
    }

    public void AddMcpServer(string name)
    {
        if (ActiveMcpServerNames.Contains(name)) return;
        // Accept any capability the pipeline offers — Lumi's own servers and runtime-discovered ones.
        var capability = GetCapabilities().FindMcpServer(name);
        if (capability is null || !capability.IsEnabled) return;
        ActiveMcpServerNames.Add(name);
        ActiveMcpChips.Add(ToMcpChip(capability));
        SyncActiveMcpsToChat();
        _ = SetMcpEnabledOnSessionAsync(name, enabled: true);
    }

    /// <summary>Registers an MCP server name without adding a chip (composer already added it).</summary>
    public void RegisterMcpByName(string name)
    {
        if (ActiveMcpServerNames.Contains(name)) return;
        var capability = GetCapabilities().FindMcpServer(name);
        if (capability is null || !capability.IsEnabled) return;
        ActiveMcpServerNames.Add(name);
        SyncActiveMcpsToChat();
        _ = SetMcpEnabledOnSessionAsync(name, enabled: true);
    }

    public void RemoveMcpByName(string name)
    {
        ActiveMcpServerNames.Remove(name);
        var chip = ActiveMcpChips.OfType<StrataTheme.Controls.StrataComposerChip>()
            .FirstOrDefault(c => c.Name == name);
        if (chip is not null) ActiveMcpChips.Remove(chip);
        SyncActiveMcpsToChat();
        _ = SetMcpEnabledOnSessionAsync(name, enabled: false);
    }

    /// <summary>
    /// Persists the chat's MCP selection.
    /// </summary>
    /// <param name="userCurated">
    /// True when the user changed the selection. Only then is the chat marked as explicitly
    /// curated, which stops newly discovered servers from being auto-selected later — a chat that
    /// merely inherited auto-selected servers must keep receiving new ones.
    /// </param>
    public void SyncActiveMcpsToChat(bool userCurated = true)
    {
        if (IsEditingMessage)
            return;

        if (CurrentChat is null)
        {
            // No chat exists yet, so there is nothing to persist to — but the user's choice still
            // has to survive until the first send creates one, or a later refresh re-adds the
            // server they just removed from the draft.
            if (userCurated)
                _draftMcpSelectionCurated = true;
            return;
        }

        CurrentChat.ActiveMcpServerNames = new List<string>(ActiveMcpServerNames);
        if (userCurated)
            CurrentChat.HasExplicitMcpServerSelection = true;
        // The live session is updated by the add/remove callers through the runtime's own
        // enable/disable API; this only persists what the next session is built from.
        QueueSaveChat(CurrentChat, saveIndex: true);
    }

    /// <summary>Populate ActiveMcpChips and ActiveMcpServerNames with all enabled MCP servers (default state).</summary>
    public void PopulateDefaultMcps()
    {
        IsLoadingChat = true;
        try
        {
            ActiveMcpServerNames.Clear();
            ActiveMcpChips.Clear();
            foreach (var server in GetCapabilities().UserInvocable(CapabilityKind.McpServer))
            {
                ActiveMcpServerNames.Add(server.Name);
                ActiveMcpChips.Add(ToMcpChip(server));
            }
        }
        finally
        {
            IsLoadingChat = false;
        }
    }

    private static StrataTheme.Controls.StrataComposerChip ToMcpChip(CapabilityDescriptor server)
        => ToMcpChip(server.Name, server);

    /// <summary>Builds an MCP chip, carrying the source hint whenever the capability is known.</summary>
    private static StrataTheme.Controls.StrataComposerChip ToMcpChip(string name, CapabilityDescriptor? server)
        => new(
            name,
            string.IsNullOrWhiteSpace(server?.Glyph) ? CopilotSdkCapabilityProvider.McpGlyph : server!.Glyph!,
            SecondaryText: server?.Description,
            SourceLabel: server?.SourceLabel);

    /// <summary>Selects a project by name (called from composer autocomplete).</summary>
    public void SelectProjectByName(string name)
    {
        var project = _dataStore.Data.Projects.FirstOrDefault(p => p.Name == name);
        if (project is not null)
            SetProjectId(project.Id);
    }

    /// <summary>Returns the display name of the current project, or null.</summary>
    public string? GetCurrentProjectName()
    {
        var pid = CurrentChat?.ProjectId ?? _pendingProjectId ?? ActiveProjectFilterId;
        if (!pid.HasValue) return null;
        return _dataStore.Data.Projects.FirstOrDefault(p => p.Id == pid.Value)?.Name;
    }

    /// <summary>Selects an agent by name (called from composer autocomplete).</summary>
    public void SelectAgentByName(string name)
    {
        ApplyComposerAgentSelection(name);
    }

    /// <summary>Adds a skill by name (called from composer autocomplete).</summary>
    public void AddSkillByName(string name)
    {
        var skillReference = FindSkillReferenceByName(name, GetCapabilities());
        if (skillReference is null)
            return;

        var alreadyActive = ActiveSkillChips
            .OfType<StrataComposerChip>()
            .Any(chip => chip.Name.Equals(skillReference.Name, StringComparison.OrdinalIgnoreCase));
        if (!alreadyActive)
        {
            RegisterSkillIdByName(skillReference.Name);
            ActiveSkillChips.Add(new StrataComposerChip(skillReference.Name, skillReference.Glyph));
        }
    }

    /// <summary>Finds a skill by name for display purposes (e.g. fetching icon glyph).</summary>
    public Skill? FindSkillByName(string name)
    {
        return _dataStore.Data.Skills.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public SkillReference? FindSkillReferenceByName(string name)
        => FindSkillReferenceByName(name, GetCapabilities());

    public SkillReference? FindSkillReferenceByName(string name, string? workDir)
        => FindSkillReferenceByName(
            name,
            workDir is { Length: > 0 } ? GetCapabilities(workDir) : GetCapabilities());

    private SkillReference? FindSkillReferenceByName(string name, CapabilitySnapshot capabilities)
    {
        var skill = FindSkillByName(name);
        if (skill is not null)
        {
            return new SkillReference
            {
                Name = skill.Name,
                Glyph = skill.IconGlyph,
                Description = skill.Description
            };
        }

        var externalSkill = capabilities.FindSkill(name);
        if (externalSkill is null)
            return null;

        return CreateExternalSkillReference(externalSkill);
    }

    public void AddAttachment(string filePath)
    {
        if (PendingAttachments.Contains(filePath))
            return;

        PendingAttachments.Add(filePath);
        PendingAttachmentItems.Add(new FileAttachmentItem(filePath, isRemovable: true, removeAction: RemoveAttachment));
    }

    public void RemoveAttachment(string filePath)
    {
        PendingAttachments.Remove(filePath);

        var pendingItem = PendingAttachmentItems.FirstOrDefault(item =>
            string.Equals(item.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (pendingItem is not null)
            PendingAttachmentItems.Remove(pendingItem);
    }

    private readonly FileSearchService _fileSearchService = new();

    /// <summary>
    /// Searches for files in the current working directory matching the query.
    /// Returns StrataComposerChip items where Name is the display filename,
    /// SecondaryText shows path context, and Value stores the full absolute path.
    /// </summary>
    public List<StrataTheme.Controls.StrataComposerChip> SearchFiles(
        string query,
        int maxResults = 20,
        CancellationToken cancellationToken = default)
    {
        var workDir = GetEffectiveWorkingDirectory();
        var isProjectDir = workDir != Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Require at least 1 character of query for user home (too many files otherwise)
        if (!isProjectDir && string.IsNullOrEmpty(query))
            return [];

        var maxDepth = isProjectDir ? 10 : 4;
        return _fileSearchService.Search(workDir, query, maxResults, maxDepth, cancellationToken)
            .ConvertAll(r =>
            {
                var fileName = Path.GetFileName(r.RelativePath);
                var parentPath = Path.GetDirectoryName(r.RelativePath);
                var secondaryText = string.IsNullOrWhiteSpace(parentPath)
                    ? null
                    : parentPath.Replace('\\', '/');

                return new StrataTheme.Controls.StrataComposerChip(
                    string.IsNullOrWhiteSpace(fileName) ? r.RelativePath : fileName,
                    "📄",
                    SecondaryText: secondaryText,
                    Value: r.FullPath);
            });
    }

    /// <summary>
    /// Resolves the effective working directory, checking pending/active project
    /// even before a chat is created (when CurrentChat is still null).
    /// </summary>
    private string GetEffectiveWorkingDirectory()
    {
        var pid = CurrentChat?.ProjectId ?? _pendingProjectId ?? ActiveProjectFilterId;
        var worktreePath = IsWorktreeMode ? WorktreePath : null;
        return ResolveEffectiveWorkingDirectory(pid, worktreePath);
    }

    private string GetEffectiveWorkingDirectory(Chat chat)
        => ResolveEffectiveWorkingDirectory(chat.ProjectId, chat.WorktreePath);

    /// <summary>
    /// Shared resolution for the directory a chat actually runs in. When a worktree is active the
    /// stored path is the worktree <em>root</em>; this maps the project's subpath into the worktree
    /// so <c>.github</c> instructions, skills/agents, MCP config, and the SDK working directory all
    /// resolve from the same folder they would in local mode (critical when the project working
    /// directory is a subfolder of the git root, e.g. a monorepo app).
    /// </summary>
    private string ResolveEffectiveWorkingDirectory(Guid? projectId, string? worktreePath)
        => ResolveEffectiveWorkingDirectory(_dataStore, projectId, worktreePath);

    internal static string ResolveEffectiveWorkingDirectory(
        DataStore dataStore,
        Guid? projectId,
        string? worktreePath)
    {
        var project = projectId.HasValue
            ? dataStore.Data.Projects.FirstOrDefault(p => p.Id == projectId.Value)
            : null;
        var projectDir = project is { WorkingDirectory: { Length: > 0 } dir } && Directory.Exists(dir)
            ? dir
            : null;

        if (worktreePath is { Length: > 0 } wt && Directory.Exists(wt))
            return GitService.ResolveWorktreeWorkingDirectory(wt, projectDir);

        return projectDir ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private Project? GetCurrentProject()
    {
        var pid = CurrentChat?.ProjectId ?? _pendingProjectId ?? ActiveProjectFilterId;
        return pid.HasValue
            ? _dataStore.Data.Projects.FirstOrDefault(p => p.Id == pid.Value)
            : null;
    }

    private List<Attachment>? TakePendingAttachments()
    {
        if (PendingAttachments.Count == 0) return null;
        var items = PendingAttachments.Select(fp => (Attachment)new AttachmentFile
        {
            Path = fp,
            DisplayName = Path.GetFileName(fp)
        }).ToList();
        PendingAttachments.Clear();
        PendingAttachmentItems.Clear();
        return items;
    }

    /// <summary>
    /// Rebases file attachment paths from the original project directory to the worktree.
    /// Files tagged via # resolve against the project directory when the worktree hasn't
    /// been created yet (lazy creation). This fixes those paths before sending.
    /// </summary>
    internal static void RebaseAttachmentPaths(
        List<Attachment> attachments,
        ChatMessage userMsg,
        string projectDir,
        string worktreePath)
    {
        var normalizedProjectDir = projectDir.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var normalizedWorktreePath = worktreePath.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(normalizedProjectDir, normalizedWorktreePath, pathComparison))
            return;

        for (var i = 0; i < attachments.Count; i++)
        {
            if (attachments[i] is not AttachmentFile file)
                continue;

            var path = file.Path;
            if (path.StartsWith(normalizedProjectDir, pathComparison))
            {
                var rebasedPath = normalizedWorktreePath + path[normalizedProjectDir.Length..];
                file.Path = rebasedPath;
                if (i < userMsg.Attachments.Count)
                    userMsg.Attachments[i] = rebasedPath;
            }
        }
    }
}
