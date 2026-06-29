using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GitHub.Copilot;
using Lumi.Models;
using Lumi.Services;
using StrataTheme.Controls;

namespace Lumi.ViewModels;

/// <summary>
/// Skills, MCP servers, agents, projects, and attachment management.
/// </summary>
public partial class ChatViewModel
{
    /// <summary>Whether the agent can still be changed. Always true — agents can now be switched mid-chat via SDK routing.</summary>
    public bool CanChangeAgent => true;

    public void SetActiveAgent(LumiAgent? agent)
    {
        var previousAgent = ActiveAgent;
        ActiveAgent = agent;
        if (CurrentChat is not null)
        {
            CurrentChat.AgentId = agent?.Id;
            QueueSaveChat(CurrentChat, saveIndex: true);
        }

        // If we have an active session, route through the SDK Agent API
        if (_activeSession is not null)
        {
            if (agent is not null)
                _ = SelectAgentOnSessionAsync(agent.Name);
            else if (previousAgent is not null)
                _ = DeselectAgentOnSessionAsync();
        }
    }

    /// <summary>Calls session.Rpc.Agent.SelectAsync to route turns through the specified custom agent.</summary>
    private async Task SelectAgentOnSessionAsync(string agentName)
    {
        if (_activeSession is null) return;
        try
        {
            await _activeSession.Rpc.Agent.SelectAsync(agentName);
        }
        catch { /* best effort — agent was still set locally via system prompt */ }
    }

    /// <summary>Calls session.Rpc.Agent.DeselectAsync to return the session to default routing.</summary>
    private async Task DeselectAgentOnSessionAsync()
    {
        if (_activeSession is null) return;
        try
        {
            await _activeSession.Rpc.Agent.DeselectAsync();
        }
        catch { /* best effort */ }
    }

    /// <summary>Assigns a project to the current (or next) chat. Called when a project filter is active.</summary>
    public void SetProjectId(Guid projectId)
    {
        if (CurrentChat is not null)
        {
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
            OnPropertyChanged(nameof(CurrentChat));
        }

        SyncComposerProjectSelectionFromState();
        RefreshProjectBadge();
        RefreshComposerCatalogs(); // Re-scan project-context and user Copilot agents/skills for the new project
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
            SyncComposerProjectSelectionFromState();
            RefreshProjectBadge();
            QueueRefreshCodingProjectState();
        }
    }

    /// <summary>Removes the project assignment from the current chat.</summary>
    public void ClearProjectId()
    {
        if (CurrentChat is not null)
        {
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
            OnPropertyChanged(nameof(CurrentChat));
        }

        SyncComposerProjectSelectionFromState();
        RefreshProjectBadge();
        RefreshComposerCatalogs(); // Re-scan to remove project-context and user Copilot agents/skills
        QueueRefreshCodingProjectState();
    }
    public void AddSkill(Skill skill)
    {
        if (ActiveSkillIds.Contains(skill.Id)) return;
        ActiveSkillIds.Add(skill.Id);
        ActiveSkillChips.Add(new StrataTheme.Controls.StrataComposerChip(skill.Name, skill.IconGlyph));
        // If added to an existing chat with a session, inject via next message instead of system prompt
        if (CurrentChat?.CopilotSessionId is not null)
            _pendingSkillInjections.Add(skill.Id);
        SyncActiveSkillsToChat();
    }

    /// <summary>Legacy file-based Copilot skill names from older chat metadata. New selections are not added.</summary>
    private readonly List<string> _activeExternalSkillNames = new();

    /// <summary>Registers a skill ID without adding a chip (composer already added it).</summary>
    public void RegisterSkillIdByName(string name)
    {
        var skill = _dataStore.Data.Skills.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (skill is not null)
        {
            if (ActiveSkillIds.Contains(skill.Id)) return;
            ActiveSkillIds.Add(skill.Id);
            // If added to an existing chat with a session, inject via next message
            if (CurrentChat?.CopilotSessionId is not null)
                _pendingSkillInjections.Add(skill.Id);
            SyncActiveSkillsToChat();
        }
    }

    private void SyncActiveSkillsToChat()
    {
        if (CurrentChat is not null)
        {
            CurrentChat.ActiveSkillIds = new List<Guid>(ActiveSkillIds);
            CurrentChat.ActiveExternalSkillNames = [];
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
    }

    public void AddMcpServer(string name)
    {
        if (ActiveMcpServerNames.Contains(name)) return;
        // Accept both Lumi-configured and project-context MCPs (project-context MCPs aren't in the data store)
        var isKnown = _dataStore.Data.McpServers.Any(s => s.Name == name)
                      || AvailableMcpChips.OfType<StrataTheme.Controls.StrataComposerChip>().Any(c => c.Name == name);
        if (!isKnown) return;
        ActiveMcpServerNames.Add(name);
        ActiveMcpChips.Add(new StrataTheme.Controls.StrataComposerChip(name));
        SyncActiveMcpsToChat();
    }

    /// <summary>Registers an MCP server name without adding a chip (composer already added it).</summary>
    public void RegisterMcpByName(string name)
    {
        if (ActiveMcpServerNames.Contains(name)) return;
        // Accept both Lumi-configured and project-context MCPs (project-context MCPs aren't in the data store)
        var isKnown = _dataStore.Data.McpServers.Any(s => s.Name == name)
                      || AvailableMcpChips.OfType<StrataTheme.Controls.StrataComposerChip>().Any(c => c.Name == name);
        if (!isKnown) return;
        ActiveMcpServerNames.Add(name);
        SyncActiveMcpsToChat();
    }

    public void RemoveMcpByName(string name)
    {
        ActiveMcpServerNames.Remove(name);
        var chip = ActiveMcpChips.OfType<StrataTheme.Controls.StrataComposerChip>()
            .FirstOrDefault(c => c.Name == name);
        if (chip is not null) ActiveMcpChips.Remove(chip);
        SyncActiveMcpsToChat();
    }

    public void SyncActiveMcpsToChat()
    {
        if (CurrentChat is not null)
        {
            CurrentChat.ActiveMcpServerNames = new List<string>(ActiveMcpServerNames);
            CurrentChat.HasExplicitMcpServerSelection = true;
            // MCP changes are applied at the next SDK create/resume boundary, not by rebuilding a live chat session.
            QueueSaveChat(CurrentChat, saveIndex: true);
        }
    }

    /// <summary>Populate ActiveMcpChips and ActiveMcpServerNames with all enabled MCP servers (default state).</summary>
    public void PopulateDefaultMcps()
    {
        IsLoadingChat = true;
        try
        {
            ActiveMcpServerNames.Clear();
            ActiveMcpChips.Clear();
            foreach (var server in _dataStore.Data.McpServers.Where(s => s.IsEnabled))
            {
                ActiveMcpServerNames.Add(server.Name);
                ActiveMcpChips.Add(new StrataTheme.Controls.StrataComposerChip(server.Name));
            }
        }
        finally
        {
            IsLoadingChat = false;
        }
    }

    /// <summary>Returns StrataComposerChip items for all agents (for composer autocomplete).</summary>
    public List<StrataTheme.Controls.StrataComposerChip> GetAgentChips()
    {
        return _dataStore.Data.Agents
            .Select(a => new StrataTheme.Controls.StrataComposerChip(
                a.Name,
                a.IconGlyph,
                SecondaryText: BuildChipSearchText(a.Description, a.SystemPrompt)))
            .ToList();
    }

    /// <summary>Returns StrataComposerChip items for all skills (for composer autocomplete).</summary>
    public List<StrataTheme.Controls.StrataComposerChip> GetSkillChips()
    {
        return _dataStore.Data.Skills
            .Select(s => new StrataTheme.Controls.StrataComposerChip(
                s.Name,
                s.IconGlyph,
                SecondaryText: BuildChipSearchText(s.Description, s.Content)))
            .ToList();
    }

    /// <summary>Returns StrataComposerChip items for all enabled MCP servers (for composer autocomplete).</summary>
    public List<StrataTheme.Controls.StrataComposerChip> GetMcpChips()
    {
        return _dataStore.Data.McpServers
            .Where(s => s.IsEnabled)
            .Select(s => new StrataTheme.Controls.StrataComposerChip(s.Name))
            .ToList();
    }

    /// <summary>Returns StrataComposerChip items for all projects (for composer autocomplete).</summary>
    public List<StrataTheme.Controls.StrataComposerChip> GetProjectChips()
    {
        return _dataStore.Data.Projects
            .Select(p => new StrataTheme.Controls.StrataComposerChip(
                p.Name,
                "📁",
                SecondaryText: BuildProjectInlineCompletionSecondaryText(p)))
            .ToList();
    }

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
        var skillReference = FindSkillReferenceByName(name);
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
    {
        var skill = FindSkillByName(name);
        if (skill is null)
            return null;

        return new SkillReference
        {
            Name = skill.Name,
            Glyph = skill.IconGlyph,
            Description = skill.Description
        };
    }

    public SkillReference? FindSkillReferenceByName(string name, string? workDir)
        => FindSkillReferenceByName(name);

    private SkillReference? FindSkillReferenceByName(string name, ProjectContextCatalogSnapshot projectContextCatalog)
    {
        var skill = FindSkillReferenceByName(name);
        if (skill is not null)
            return skill;

        var externalSkill = projectContextCatalog.FindSkill(name);
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
    {
        var project = projectId.HasValue
            ? _dataStore.Data.Projects.FirstOrDefault(p => p.Id == projectId.Value)
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

        if (string.Equals(normalizedProjectDir, normalizedWorktreePath, StringComparison.OrdinalIgnoreCase))
            return;

        for (var i = 0; i < attachments.Count; i++)
        {
            if (attachments[i] is not AttachmentFile file)
                continue;

            var path = file.Path;
            if (path.StartsWith(normalizedProjectDir, StringComparison.OrdinalIgnoreCase))
            {
                var rebasedPath = normalizedWorktreePath + path[normalizedProjectDir.Length..];
                file.Path = rebasedPath;
                if (i < userMsg.Attachments.Count)
                    userMsg.Attachments[i] = rebasedPath;
            }
        }
    }
}
