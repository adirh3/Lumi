using System;
using System.Collections.Generic;
using System.Linq;
using Lumi.Models;

namespace Lumi.Services;

public sealed record FeatureChangeResult(
    string Message,
    bool DataChanged = false,
    bool SyncSkillFiles = false,
    string? RenamedMcpOldName = null,
    string? RenamedMcpNewName = null,
    string? DeletedMcpName = null);

public sealed class LumiFeatureManager
{
    private readonly DataStore _dataStore;

    public LumiFeatureManager(DataStore dataStore)
    {
        _dataStore = dataStore;
    }

    public FeatureChangeResult ManageProjects(
        string action,
        string? identifier = null,
        string? name = null,
        string? instructions = null,
        string? workingDirectory = null,
        bool? clearWorkingDirectory = null,
        string[]? additionalContextDirectories = null,
        bool? clearAdditionalContextDirectories = null,
        string? query = null)
    {
        return NormalizeAction(action) switch
        {
            "list" => new FeatureChangeResult(ListProjects(query ?? identifier)),
            "create" => CreateProject(name, instructions, workingDirectory, additionalContextDirectories),
            "update" => UpdateProject(identifier, name, instructions, workingDirectory, clearWorkingDirectory,
                additionalContextDirectories, clearAdditionalContextDirectories),
            "delete" => DeleteProject(identifier),
            _ => InvalidAction("projects", action)
        };
    }

    public FeatureChangeResult ManageSkills(
        string action,
        string? identifier = null,
        string? name = null,
        string? description = null,
        string? content = null,
        string? iconGlyph = null,
        string? query = null)
    {
        return NormalizeAction(action) switch
        {
            "list" => new FeatureChangeResult(ListSkills(query ?? identifier)),
            "create" => CreateSkill(name, description, content, iconGlyph),
            "update" => UpdateSkill(identifier, name, description, content, iconGlyph),
            "delete" => DeleteSkill(identifier),
            _ => InvalidAction("skills", action)
        };
    }

    public FeatureChangeResult ManageLumis(
        string action,
        string? identifier = null,
        string? name = null,
        string? description = null,
        string? systemPrompt = null,
        string? iconGlyph = null,
        string[]? skillIdentifiers = null,
        string[]? toolNames = null,
        string[]? mcpServerIdentifiers = null,
        string? query = null)
    {
        return NormalizeAction(action) switch
        {
            "list" => new FeatureChangeResult(ListLumis(query ?? identifier)),
            "create" => CreateLumi(name, description, systemPrompt, iconGlyph, skillIdentifiers, toolNames, mcpServerIdentifiers),
            "update" => UpdateLumi(identifier, name, description, systemPrompt, iconGlyph, skillIdentifiers, toolNames, mcpServerIdentifiers),
            "delete" => DeleteLumi(identifier),
            _ => InvalidAction("Lumis", action)
        };
    }

    public FeatureChangeResult ManageMcps(
        string action,
        string? identifier = null,
        string? name = null,
        string? description = null,
        string? serverType = null,
        string? command = null,
        string[]? args = null,
        string? url = null,
        string[]? envEntries = null,
        string[]? headerEntries = null,
        string[]? toolNames = null,
        int? timeout = null,
        bool? clearTimeout = null,
        bool? isEnabled = null,
        string? query = null)
    {
        return NormalizeAction(action) switch
        {
            "list" => new FeatureChangeResult(ListMcps(query ?? identifier)),
            "create" => CreateMcp(name, description, serverType, command, args, url, envEntries, headerEntries, toolNames, timeout, isEnabled),
            "update" => UpdateMcp(identifier, name, description, serverType, command, args, url, envEntries, headerEntries, toolNames, timeout, clearTimeout, isEnabled),
            "delete" => DeleteMcp(identifier),
            _ => InvalidAction("MCP servers", action)
        };
    }

    public FeatureChangeResult ManageMemories(
        string action,
        string? identifier = null,
        string? key = null,
        string? content = null,
        string? category = null,
        string? query = null)
    {
        return NormalizeAction(action) switch
        {
            "list" => new FeatureChangeResult(ListMemories(query ?? identifier)),
            "create" => CreateMemory(key, content, category),
            "update" => UpdateMemory(identifier, key, content, category),
            "delete" => DeleteMemory(identifier),
            _ => InvalidAction("memories", action)
        };
    }

    public FeatureChangeResult ManageJobs(
        string action,
        string? identifier = null,
        string? name = null,
        string? description = null,
        string? prompt = null,
        string? chatIdentifier = null,
        string? triggerType = null,
        string? scheduleType = null,
        int? intervalMinutes = null,
        string? dailyTime = null,
        string? daysOfWeek = null,
        int? monthlyDay = null,
        string? cronExpression = null,
        string? runAt = null,
        string? scriptContent = null,
        string? scriptLanguage = null,
        bool? isTemporary = null,
        bool? isEnabled = null,
        bool? runNow = null,
        string? query = null,
        Guid? defaultChatId = null)
    {
        var normalizedAction = NormalizeOrNull(action)?.ToLowerInvariant() ?? "";
        return normalizedAction switch
        {
            "list" or "show" or "search" => new FeatureChangeResult(ListJobs(query ?? identifier)),
            "create" or "add" or "new" => CreateJob(name, description, prompt, chatIdentifier, triggerType, scheduleType,
                intervalMinutes, dailyTime, daysOfWeek, monthlyDay, cronExpression, runAt, scriptContent, scriptLanguage,
                isTemporary, isEnabled, runNow, defaultChatId),
            "update" or "edit" or "rename" or "modify" => UpdateJob(identifier, name, description, prompt, chatIdentifier,
                triggerType, scheduleType, intervalMinutes, dailyTime, daysOfWeek, monthlyDay, cronExpression, runAt,
                scriptContent, scriptLanguage, isTemporary, isEnabled, runNow, defaultChatId),
            "delete" or "remove" => DeleteJob(identifier),
            "pause" or "disable" => SetJobEnabled(identifier, enabled: false),
            "resume" or "enable" => SetJobEnabled(identifier, enabled: true),
            "run" or "run_now" or "run-now" => RunJobSoon(identifier),
            _ => InvalidAction("background jobs", action)
        };
    }

    private FeatureChangeResult CreateJob(
        string? name,
        string? description,
        string? prompt,
        string? chatIdentifier,
        string? triggerType,
        string? scheduleType,
        int? intervalMinutes,
        string? dailyTime,
        string? daysOfWeek,
        int? monthlyDay,
        string? cronExpression,
        string? runAt,
        string? scriptContent,
        string? scriptLanguage,
        bool? isTemporary,
        bool? isEnabled,
        bool? runNow,
        Guid? defaultChatId)
    {
        var normalizedName = NormalizeOrNull(name);
        var normalizedPrompt = NormalizeOrNull(prompt);
        if (normalizedName is null)
            return Failure("Background job name is required.");
        if (normalizedPrompt is null)
            return Failure("Background job prompt/instructions are required.");
        var jobs = _dataStore.SnapshotBackgroundJobs();
        if (HasConflictingLabel(jobs, normalizedName, static job => job.Name, static job => job.Id))
            return Failure($"A background job named \"{normalizedName}\" already exists.");

        var chatLookup = ResolveChat(chatIdentifier, defaultChatId);
        if (!chatLookup.Success)
            return Failure(chatLookup.Error!);

        var normalizedTriggerType = triggerType is null && !string.IsNullOrWhiteSpace(scriptContent)
            ? BackgroundJobTriggerTypes.Script
            : BackgroundJobSchedule.NormalizeTriggerType(triggerType);
        var job = new BackgroundJob
        {
            Name = normalizedName,
            Description = NormalizeOrNull(description) ?? "",
            Prompt = normalizedPrompt,
            ChatId = chatLookup.Item!.Id,
            TriggerType = normalizedTriggerType,
            ScheduleType = scheduleType ?? BackgroundJobScheduleTypes.Interval,
            IntervalMinutes = intervalMinutes ?? 1440,
            DailyTime = NormalizeOrNull(dailyTime) ?? "08:00",
            DaysOfWeek = NormalizeOrNull(daysOfWeek) ?? "Mon,Tue,Wed,Thu,Fri",
            MonthlyDay = monthlyDay ?? 1,
            CronExpression = NormalizeOrNull(cronExpression) ?? "0 8 * * *",
            ScriptContent = NormalizeOrNull(scriptContent) ?? "",
            ScriptLanguage = NormalizeOrNull(scriptLanguage) ?? BackgroundJobScriptLanguages.PowerShell,
            IsTemporary = normalizedTriggerType == BackgroundJobTriggerTypes.Script || isTemporary == true,
            IsEnabled = isEnabled ?? true
        };

        if (!TrySetRunAt(job, runAt, out var runAtError))
            return Failure(runAtError!);

        if (!TryValidateJobConfiguration(job, out var configurationError))
            return Failure(configurationError!);
        BackgroundJobSchedule.Normalize(job);

        var now = DateTimeOffset.Now;
        job.NextRunAt = job.IsEnabled
            ? job.TriggerType == BackgroundJobTriggerTypes.Script || runNow == true
                ? now
                : BackgroundJobSchedule.ComputeNextRun(job, now, afterRun: false)
            : null;

        _dataStore.AddBackgroundJob(job);
        return Success($"Background job created.\n{DescribeJob(job)}");
    }

    private FeatureChangeResult UpdateJob(
        string? identifier,
        string? name,
        string? description,
        string? prompt,
        string? chatIdentifier,
        string? triggerType,
        string? scheduleType,
        int? intervalMinutes,
        string? dailyTime,
        string? daysOfWeek,
        int? monthlyDay,
        string? cronExpression,
        string? runAt,
        string? scriptContent,
        string? scriptLanguage,
        bool? isTemporary,
        bool? isEnabled,
        bool? runNow,
        Guid? defaultChatId)
    {
        var lookup = ResolveByIdOrLabel(
            _dataStore.SnapshotBackgroundJobs(),
            identifier,
            static job => job.Id,
            static job => job.Name,
            "background job");
        if (!lookup.Success)
            return Failure(lookup.Error!);

        if (name is null && description is null && prompt is null && chatIdentifier is null
            && triggerType is null && scheduleType is null && intervalMinutes is null && dailyTime is null
            && daysOfWeek is null && monthlyDay is null && cronExpression is null && runAt is null
            && scriptContent is null && scriptLanguage is null
            && isTemporary is null && isEnabled is null && runNow is null)
            return Failure("No background job changes were provided.");

        var job = lookup.Item!;

        lock (job.SyncRoot)
        {
        if (name is not null)
        {
            var normalizedName = NormalizeOrNull(name);
            if (normalizedName is null)
                return Failure("Background job name cannot be empty.");
            if (HasConflictingLabel(_dataStore.SnapshotBackgroundJobs(), normalizedName, static item => item.Name, static item => item.Id, job.Id))
                return Failure($"A background job named \"{normalizedName}\" already exists.");
            job.Name = normalizedName;
        }

        if (description is not null)
            job.Description = NormalizeOrNull(description) ?? "";

        if (prompt is not null)
        {
            var normalizedPrompt = NormalizeOrNull(prompt);
            if (normalizedPrompt is null)
                return Failure("Background job prompt cannot be empty.");
            job.Prompt = normalizedPrompt;
        }

        if (chatIdentifier is not null)
        {
            var chatLookup = ResolveChat(chatIdentifier, defaultChatId);
            if (!chatLookup.Success)
                return Failure(chatLookup.Error!);
            job.ChatId = chatLookup.Item!.Id;
        }

        if (triggerType is not null)
            job.TriggerType = triggerType;
        if (scheduleType is not null)
            job.ScheduleType = scheduleType;
        if (intervalMinutes.HasValue)
            job.IntervalMinutes = intervalMinutes.Value;
        if (dailyTime is not null)
            job.DailyTime = NormalizeOrNull(dailyTime) ?? "08:00";
        if (daysOfWeek is not null)
            job.DaysOfWeek = NormalizeOrNull(daysOfWeek) ?? "Mon,Tue,Wed,Thu,Fri";
        if (monthlyDay.HasValue)
            job.MonthlyDay = monthlyDay.Value;
        if (cronExpression is not null)
            job.CronExpression = NormalizeOrNull(cronExpression) ?? "0 8 * * *";
        if (scriptContent is not null)
            job.ScriptContent = NormalizeOrNull(scriptContent) ?? "";
        if (scriptLanguage is not null)
            job.ScriptLanguage = NormalizeOrNull(scriptLanguage) ?? BackgroundJobScriptLanguages.PowerShell;
        if (isTemporary.HasValue)
            job.IsTemporary = isTemporary.Value;
        if (isEnabled.HasValue)
            job.IsEnabled = isEnabled.Value;

        if (!TrySetRunAt(job, runAt, out var runAtError))
            return Failure(runAtError!);

        if (!TryValidateJobConfiguration(job, out var configurationError))
            return Failure(configurationError!);
        BackgroundJobSchedule.Normalize(job);
        if (job.TriggerType == BackgroundJobTriggerTypes.Script)
            job.IsTemporary = true;

        var now = DateTimeOffset.Now;
        job.NextRunAt = job.IsEnabled
            ? job.TriggerType == BackgroundJobTriggerTypes.Script || runNow == true
                ? now
                : BackgroundJobSchedule.ComputeNextRun(job, now, afterRun: false)
            : null;
        job.UpdatedAt = now;
        }

        _dataStore.MarkBackgroundJobsChanged();

        return Success($"Background job updated.\n{DescribeJob(job)}");
    }

    private static bool TryValidateJobConfiguration(BackgroundJob job, out string? error)
    {
        error = null;
        var triggerType = BackgroundJobSchedule.NormalizeTriggerType(job.TriggerType);
        var scheduleType = BackgroundJobSchedule.NormalizeScheduleType(job.ScheduleType);

        if (triggerType == BackgroundJobTriggerTypes.Script)
        {
            if (string.IsNullOrWhiteSpace(job.ScriptContent))
            {
                error = "Script background jobs require scriptContent.";
                return false;
            }

            return true;
        }

        if ((scheduleType == BackgroundJobScheduleTypes.Daily
             || scheduleType == BackgroundJobScheduleTypes.Weekly
             || scheduleType == BackgroundJobScheduleTypes.Monthly)
            && !BackgroundJobSchedule.TryValidateDailyTime(job.DailyTime, out var timeError))
        {
            error = timeError;
            return false;
        }

        if (scheduleType == BackgroundJobScheduleTypes.Weekly
            && !BackgroundJobSchedule.TryValidateDaysOfWeek(job.DaysOfWeek, out var daysError))
        {
            error = daysError;
            return false;
        }

        if (scheduleType == BackgroundJobScheduleTypes.Monthly
            && (job.MonthlyDay < 1 || job.MonthlyDay > 31))
        {
            error = "Monthly day must be between 1 and 31.";
            return false;
        }

        if (scheduleType == BackgroundJobScheduleTypes.Cron
            && !BackgroundJobSchedule.TryValidateCronExpression(job.CronExpression, out var cronError))
        {
            error = cronError;
            return false;
        }

        return true;
    }

    private FeatureChangeResult DeleteJob(string? identifier)
    {
        var lookup = ResolveByIdOrLabel(
            _dataStore.SnapshotBackgroundJobs(),
            identifier,
            static job => job.Id,
            static job => job.Name,
            "background job");
        if (!lookup.Success)
            return Failure(lookup.Error!);

        var job = lookup.Item!;
        _dataStore.RemoveBackgroundJob(job);
        return Success($"Background job deleted: {job.Name}.");
    }

    private FeatureChangeResult SetJobEnabled(string? identifier, bool enabled)
    {
        var lookup = ResolveByIdOrLabel(
            _dataStore.SnapshotBackgroundJobs(),
            identifier,
            static job => job.Id,
            static job => job.Name,
            "background job");
        if (!lookup.Success)
            return Failure(lookup.Error!);

        var job = lookup.Item!;
        lock (job.SyncRoot)
        {
            job.IsEnabled = enabled;
            job.NextRunAt = enabled ? BackgroundJobSchedule.ComputeNextRun(job, DateTimeOffset.Now, afterRun: false) : null;
            job.UpdatedAt = DateTimeOffset.Now;
        }

        _dataStore.MarkBackgroundJobsChanged();
        return Success(enabled ? $"Background job resumed.\n{DescribeJob(job)}" : $"Background job paused.\n{DescribeJob(job)}");
    }

    private FeatureChangeResult RunJobSoon(string? identifier)
    {
        var lookup = ResolveByIdOrLabel(
            _dataStore.SnapshotBackgroundJobs(),
            identifier,
            static job => job.Id,
            static job => job.Name,
            "background job");
        if (!lookup.Success)
            return Failure(lookup.Error!);

        var job = lookup.Item!;
        lock (job.SyncRoot)
        {
            job.IsEnabled = true;
            job.NextRunAt = DateTimeOffset.Now;
            job.UpdatedAt = DateTimeOffset.Now;
        }

        _dataStore.MarkBackgroundJobsChanged();
        return Success($"Background job queued to run now.\n{DescribeJob(job)}");
    }

    private string ListJobs(string? query)
    {
        var jobs = FilterByQuery(
                _dataStore.SnapshotBackgroundJobs(),
                query,
                job => job.Name,
                job => job.Description,
                job => job.Prompt,
                job => job.LastRunSummary)
            .OrderBy(job => job.NextRunAt ?? DateTimeOffset.MaxValue)
            .ThenBy(job => job.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (jobs.Count == 0)
            return string.IsNullOrWhiteSpace(query) ? "No background jobs found." : $"No background jobs matched \"{query}\".";

        return "Background jobs:\n" + string.Join("\n", jobs.Select(DescribeJob));
    }

    private FeatureChangeResult CreateProject(
        string? name,
        string? instructions,
        string? workingDirectory,
        string[]? additionalContextDirectories)
    {
        var normalizedName = NormalizeOrNull(name);
        if (normalizedName is null)
            return Failure("Project name is required.");
        if (HasConflictingLabel(_dataStore.Data.Projects, normalizedName, static project => project.Name, static project => project.Id))
            return Failure($"A project named \"{normalizedName}\" already exists.");

        var project = new Project
        {
            Name = normalizedName,
            Instructions = NormalizeOrNull(instructions) ?? "",
            WorkingDirectory = NormalizeOrNull(workingDirectory),
            AdditionalContextDirectories = ProjectContextDirectoryHelper.NormalizeFolderList(additionalContextDirectories)
        };

        _dataStore.Data.Projects.Add(project);
        return Success($"Project created: {project.Name}.");
    }

    private FeatureChangeResult UpdateProject(
        string? identifier,
        string? name,
        string? instructions,
        string? workingDirectory,
        bool? clearWorkingDirectory,
        string[]? additionalContextDirectories,
        bool? clearAdditionalContextDirectories)
    {
        var lookup = ResolveByIdOrLabel(
            _dataStore.Data.Projects,
            identifier,
            static project => project.Id,
            static project => project.Name,
            "project");
        if (!lookup.Success)
            return Failure(lookup.Error!);

        var project = lookup.Item!;
        if (name is null
            && instructions is null
            && workingDirectory is null
            && clearWorkingDirectory != true
            && additionalContextDirectories is null
            && clearAdditionalContextDirectories != true)
            return Failure("No project changes were provided.");

        if (name is not null)
        {
            var normalizedName = NormalizeOrNull(name);
            if (normalizedName is null)
                return Failure("Project name cannot be empty.");
            if (HasConflictingLabel(_dataStore.Data.Projects, normalizedName, static item => item.Name, static item => item.Id, project.Id))
                return Failure($"A project named \"{normalizedName}\" already exists.");
            project.Name = normalizedName;
        }

        if (instructions is not null)
            project.Instructions = NormalizeOrNull(instructions) ?? "";

        if (clearWorkingDirectory == true)
            project.WorkingDirectory = null;
        else if (workingDirectory is not null)
            project.WorkingDirectory = NormalizeOrNull(workingDirectory);

        if (clearAdditionalContextDirectories == true)
            project.AdditionalContextDirectories = [];
        else if (additionalContextDirectories is not null)
            project.AdditionalContextDirectories = ProjectContextDirectoryHelper.NormalizeFolderList(additionalContextDirectories);

        return Success($"Project updated.\n{DescribeProject(project)}");
    }

    private FeatureChangeResult DeleteProject(string? identifier)
    {
        var lookup = ResolveByIdOrLabel(
            _dataStore.Data.Projects,
            identifier,
            static project => project.Id,
            static project => project.Name,
            "project");
        if (!lookup.Success)
            return Failure(lookup.Error!);

        var project = lookup.Item!;
        var affectedChats = 0;
        foreach (var chat in _dataStore.Data.Chats.Where(c => c.ProjectId == project.Id))
        {
            chat.ProjectId = null;
            _dataStore.MarkChatChanged(chat);
            affectedChats++;
        }

        _dataStore.Data.Projects.Remove(project);
        return Success($"Project deleted: {project.Name}. Unassigned {affectedChats} chat(s).");
    }

    private string ListProjects(string? query)
    {
        var projects = FilterByQuery(
                _dataStore.Data.Projects,
                query,
                project => project.Name,
                project => project.Instructions,
                project => project.WorkingDirectory,
                project => ProjectContextDirectoryHelper.FormatFolderList(project.AdditionalContextDirectories))
            .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (projects.Count == 0)
            return string.IsNullOrWhiteSpace(query) ? "No Lumi projects found." : $"No Lumi projects matched \"{query}\".";

        return "Projects:\n" + string.Join("\n", projects.Select(DescribeProject));
    }

    private FeatureChangeResult CreateSkill(string? name, string? description, string? content, string? iconGlyph)
    {
        var normalizedName = NormalizeOrNull(name);
        var normalizedContent = NormalizeOrNull(content);
        if (normalizedName is null)
            return Failure("Skill name is required.");
        if (normalizedContent is null)
            return Failure("Skill content is required.");
        if (HasConflictingLabel(_dataStore.Data.Skills, normalizedName, static skill => skill.Name, static skill => skill.Id))
            return Failure($"A skill named \"{normalizedName}\" already exists.");

        var skill = new Skill
        {
            Name = normalizedName,
            Description = NormalizeOrNull(description) ?? "",
            Content = normalizedContent,
            IconGlyph = NormalizeOrNull(iconGlyph) ?? "⚡"
        };

        _dataStore.Data.Skills.Add(skill);
        return Success($"Skill created.\n{DescribeSkill(skill)}", syncSkillFiles: true);
    }

    private FeatureChangeResult UpdateSkill(
        string? identifier,
        string? name,
        string? description,
        string? content,
        string? iconGlyph)
    {
        var lookup = ResolveByIdOrLabel(
            _dataStore.Data.Skills,
            identifier,
            static skill => skill.Id,
            static skill => skill.Name,
            "skill");
        if (!lookup.Success)
            return Failure(lookup.Error!);

        var skill = lookup.Item!;
        if (name is null && description is null && content is null && iconGlyph is null)
            return Failure("No skill changes were provided.");

        if (name is not null)
        {
            var normalizedName = NormalizeOrNull(name);
            if (normalizedName is null)
                return Failure("Skill name cannot be empty.");
            if (HasConflictingLabel(_dataStore.Data.Skills, normalizedName, static item => item.Name, static item => item.Id, skill.Id))
                return Failure($"A skill named \"{normalizedName}\" already exists.");
            skill.Name = normalizedName;
        }

        if (description is not null)
            skill.Description = NormalizeOrNull(description) ?? "";

        if (content is not null)
        {
            var normalizedContent = NormalizeOrNull(content);
            if (normalizedContent is null)
                return Failure("Skill content cannot be empty.");
            skill.Content = normalizedContent;
        }

        if (iconGlyph is not null)
            skill.IconGlyph = NormalizeOrNull(iconGlyph) ?? "⚡";

        return Success($"Skill updated.\n{DescribeSkill(skill)}", syncSkillFiles: true);
    }

    private FeatureChangeResult DeleteSkill(string? identifier)
    {
        var lookup = ResolveByIdOrLabel(
            _dataStore.Data.Skills,
            identifier,
            static skill => skill.Id,
            static skill => skill.Name,
            "skill");
        if (!lookup.Success)
            return Failure(lookup.Error!);

        var skill = lookup.Item!;
        var removedFromAgents = 0;
        foreach (var agent in _dataStore.Data.Agents)
        {
            if (agent.SkillIds.Remove(skill.Id))
                removedFromAgents++;
        }

        var removedFromChats = 0;
        foreach (var chat in _dataStore.Data.Chats)
        {
            if (chat.ActiveSkillIds.Remove(skill.Id))
            {
                _dataStore.MarkChatChanged(chat);
                removedFromChats++;
            }
        }

        _dataStore.Data.Skills.Remove(skill);
        return Success(
            $"Skill deleted: {skill.Name}. Removed from {removedFromAgents} Lumi(s) and {removedFromChats} chat(s).",
            syncSkillFiles: true);
    }

    private string ListSkills(string? query)
    {
        var skills = FilterByQuery(
                _dataStore.Data.Skills,
                query,
                skill => skill.Name,
                skill => skill.Description)
            .OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (skills.Count == 0)
            return string.IsNullOrWhiteSpace(query) ? "No Lumi skills found." : $"No Lumi skills matched \"{query}\".";

        return "Skills:\n" + string.Join("\n", skills.Select(DescribeSkill));
    }

    private FeatureChangeResult CreateLumi(
        string? name,
        string? description,
        string? systemPrompt,
        string? iconGlyph,
        string[]? skillIdentifiers,
        string[]? toolNames,
        string[]? mcpServerIdentifiers)
    {
        var normalizedName = NormalizeOrNull(name);
        if (normalizedName is null)
            return Failure("Lumi name is required.");
        if (HasConflictingLabel(_dataStore.Data.Agents, normalizedName, static agent => agent.Name, static agent => agent.Id))
            return Failure($"A Lumi named \"{normalizedName}\" already exists.");

        var skillIdsResult = ResolveSkillIds(skillIdentifiers);
        if (!skillIdsResult.Success)
            return Failure(skillIdsResult.Error!);

        var mcpIdsResult = ResolveMcpIds(mcpServerIdentifiers);
        if (!mcpIdsResult.Success)
            return Failure(mcpIdsResult.Error!);

        var agent = new LumiAgent
        {
            Name = normalizedName,
            Description = NormalizeOrNull(description) ?? "",
            SystemPrompt = NormalizeOrNull(systemPrompt) ?? "",
            IconGlyph = NormalizeOrNull(iconGlyph) ?? "✦",
            SkillIds = skillIdsResult.Items!,
            ToolNames = NormalizeList(toolNames),
            McpServerIds = mcpIdsResult.Items!
        };

        _dataStore.Data.Agents.Add(agent);
        return Success($"Lumi created.\n{DescribeLumi(agent)}");
    }

    private FeatureChangeResult UpdateLumi(
        string? identifier,
        string? name,
        string? description,
        string? systemPrompt,
        string? iconGlyph,
        string[]? skillIdentifiers,
        string[]? toolNames,
        string[]? mcpServerIdentifiers)
    {
        var lookup = ResolveByIdOrLabel(
            _dataStore.Data.Agents,
            identifier,
            static agent => agent.Id,
            static agent => agent.Name,
            "Lumi");
        if (!lookup.Success)
            return Failure(lookup.Error!);

        var agent = lookup.Item!;
        if (name is null && description is null && systemPrompt is null && iconGlyph is null
            && skillIdentifiers is null && toolNames is null && mcpServerIdentifiers is null)
            return Failure("No Lumi changes were provided.");

        if (name is not null)
        {
            var normalizedName = NormalizeOrNull(name);
            if (normalizedName is null)
                return Failure("Lumi name cannot be empty.");
            if (HasConflictingLabel(_dataStore.Data.Agents, normalizedName, static item => item.Name, static item => item.Id, agent.Id))
                return Failure($"A Lumi named \"{normalizedName}\" already exists.");
            agent.Name = normalizedName;
        }

        if (description is not null)
            agent.Description = NormalizeOrNull(description) ?? "";

        if (systemPrompt is not null)
            agent.SystemPrompt = NormalizeOrNull(systemPrompt) ?? "";

        if (iconGlyph is not null)
            agent.IconGlyph = NormalizeOrNull(iconGlyph) ?? "✦";

        if (skillIdentifiers is not null)
        {
            var skillIdsResult = ResolveSkillIds(skillIdentifiers);
            if (!skillIdsResult.Success)
                return Failure(skillIdsResult.Error!);
            agent.SkillIds = skillIdsResult.Items!;
        }

        if (toolNames is not null)
            agent.ToolNames = NormalizeList(toolNames);

        if (mcpServerIdentifiers is not null)
        {
            var mcpIdsResult = ResolveMcpIds(mcpServerIdentifiers);
            if (!mcpIdsResult.Success)
                return Failure(mcpIdsResult.Error!);
            agent.McpServerIds = mcpIdsResult.Items!;
        }

        return Success($"Lumi updated.\n{DescribeLumi(agent)}");
    }

    private FeatureChangeResult DeleteLumi(string? identifier)
    {
        var lookup = ResolveByIdOrLabel(
            _dataStore.Data.Agents,
            identifier,
            static agent => agent.Id,
            static agent => agent.Name,
            "Lumi");
        if (!lookup.Success)
            return Failure(lookup.Error!);

        var agent = lookup.Item!;
        var affectedChats = 0;
        foreach (var chat in _dataStore.Data.Chats.Where(c => c.AgentId == agent.Id))
        {
            chat.AgentId = null;
            _dataStore.MarkChatChanged(chat);
            affectedChats++;
        }

        _dataStore.Data.Agents.Remove(agent);
        return Success($"Lumi deleted: {agent.Name}. Unassigned {affectedChats} chat(s).");
    }

    private string ListLumis(string? query)
    {
        var agents = FilterByQuery(
                _dataStore.Data.Agents,
                query,
                agent => agent.Name,
                agent => agent.Description,
                agent => agent.SystemPrompt)
            .OrderBy(agent => agent.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (agents.Count == 0)
            return string.IsNullOrWhiteSpace(query) ? "No Lumi agents found." : $"No Lumi agents matched \"{query}\".";

        return "Lumis:\n" + string.Join("\n", agents.Select(DescribeLumi));
    }

    private FeatureChangeResult CreateMcp(
        string? name,
        string? description,
        string? serverType,
        string? command,
        string[]? args,
        string? url,
        string[]? envEntries,
        string[]? headerEntries,
        string[]? toolNames,
        int? timeout,
        bool? isEnabled)
    {
        var normalizedName = NormalizeOrNull(name);
        if (normalizedName is null)
            return Failure("MCP server name is required.");
        if (HasConflictingLabel(_dataStore.Data.McpServers, normalizedName, static server => server.Name, static server => server.Id))
            return Failure($"An MCP server named \"{normalizedName}\" already exists.");

        var normalizedType = serverType is null ? "local" : NormalizeServerType(serverType);
        if (normalizedType is null)
            return Failure("MCP server type must be \"local\" or \"remote\".");

        if (!TryParseKeyValueEntries(envEntries, out var env, out var envError))
            return Failure(envError!);
        if (!TryParseKeyValueEntries(headerEntries, out var headers, out var headerError))
            return Failure(headerError!);
        if (timeout is < 0)
            return Failure("MCP timeout cannot be negative.");

        var normalizedCommand = NormalizeOrNull(command);
        var normalizedUrl = NormalizeOrNull(url);

        if (normalizedType == "local" && normalizedCommand is null)
            return Failure("Local MCP servers require a command.");
        if (normalizedType == "remote" && normalizedUrl is null)
            return Failure("Remote MCP servers require a URL.");

        var server = new McpServer
        {
            Name = normalizedName,
            Description = NormalizeOrNull(description) ?? "",
            ServerType = normalizedType,
            Command = normalizedType == "local" ? normalizedCommand! : "",
            Args = NormalizeList(args),
            Url = normalizedType == "remote" ? normalizedUrl! : "",
            Env = normalizedType == "local" ? env! : [],
            Headers = normalizedType == "remote" ? headers! : [],
            Tools = NormalizeList(toolNames),
            Timeout = timeout,
            IsEnabled = isEnabled ?? true
        };

        _dataStore.Data.McpServers.Add(server);
        return Success($"MCP server created.\n{DescribeMcp(server)}");
    }

    private FeatureChangeResult UpdateMcp(
        string? identifier,
        string? name,
        string? description,
        string? serverType,
        string? command,
        string[]? args,
        string? url,
        string[]? envEntries,
        string[]? headerEntries,
        string[]? toolNames,
        int? timeout,
        bool? clearTimeout,
        bool? isEnabled)
    {
        var lookup = ResolveByIdOrLabel(
            _dataStore.Data.McpServers,
            identifier,
            static server => server.Id,
            static server => server.Name,
            "MCP server");
        if (!lookup.Success)
            return Failure(lookup.Error!);

        var server = lookup.Item!;
        if (name is null && description is null && serverType is null && command is null && args is null
            && url is null && envEntries is null && headerEntries is null && toolNames is null
            && timeout is null && clearTimeout != true && isEnabled is null)
            return Failure("No MCP server changes were provided.");

        var previousName = server.Name;
        var previousType = server.ServerType;
        var normalizedType = serverType is null ? server.ServerType : NormalizeServerType(serverType);
        if (serverType is not null && normalizedType is null)
            return Failure("MCP server type must be \"local\" or \"remote\".");

        if (!TryParseKeyValueEntries(envEntries, out var env, out var envError))
            return Failure(envError!);
        if (!TryParseKeyValueEntries(headerEntries, out var headers, out var headerError))
            return Failure(headerError!);
        if (timeout is < 0)
            return Failure("MCP timeout cannot be negative.");

        var normalizedCommand = command is null ? null : NormalizeOrNull(command);
        var normalizedUrl = url is null ? null : NormalizeOrNull(url);

        if (normalizedType == "local" && normalizedCommand is null && previousType != "local")
            return Failure("Switching an MCP server to local requires a command.");
        if (normalizedType == "remote" && normalizedUrl is null && previousType != "remote")
            return Failure("Switching an MCP server to remote requires a URL.");

        if (name is not null)
        {
            var normalizedName = NormalizeOrNull(name);
            if (normalizedName is null)
                return Failure("MCP server name cannot be empty.");
            if (HasConflictingLabel(_dataStore.Data.McpServers, normalizedName, static item => item.Name, static item => item.Id, server.Id))
                return Failure($"An MCP server named \"{normalizedName}\" already exists.");
            server.Name = normalizedName;
        }

        if (description is not null)
            server.Description = NormalizeOrNull(description) ?? "";

        server.ServerType = normalizedType!;
        if (normalizedType == "local")
        {
            if (command is not null)
                server.Command = normalizedCommand ?? "";
            else if (previousType != "local")
                server.Command = "";

            if (args is not null)
                server.Args = NormalizeList(args);
            else if (previousType != "local")
                server.Args = [];

            if (envEntries is not null)
                server.Env = env!;
            else if (previousType != "local")
                server.Env = [];

            server.Url = "";
            server.Headers = [];
        }
        else
        {
            if (url is not null)
                server.Url = normalizedUrl ?? "";
            else if (previousType != "remote")
                server.Url = "";

            if (headerEntries is not null)
                server.Headers = headers!;
            else if (previousType != "remote")
                server.Headers = [];

            server.Command = "";
            server.Args = [];
            server.Env = [];
        }

        if (toolNames is not null)
            server.Tools = NormalizeList(toolNames);

        if (timeout is not null)
            server.Timeout = timeout;
        else if (clearTimeout == true)
            server.Timeout = null;

        if (isEnabled.HasValue)
            server.IsEnabled = isEnabled.Value;

        if (!string.Equals(previousName, server.Name, StringComparison.Ordinal))
            RenameMcpInChats(previousName, server.Name);

        return Success(
            $"MCP server updated.\n{DescribeMcp(server)}",
            renamedMcpOldName: !string.Equals(previousName, server.Name, StringComparison.Ordinal) ? previousName : null,
            renamedMcpNewName: !string.Equals(previousName, server.Name, StringComparison.Ordinal) ? server.Name : null);
    }

    private FeatureChangeResult DeleteMcp(string? identifier)
    {
        var lookup = ResolveByIdOrLabel(
            _dataStore.Data.McpServers,
            identifier,
            static server => server.Id,
            static server => server.Name,
            "MCP server");
        if (!lookup.Success)
            return Failure(lookup.Error!);

        var server = lookup.Item!;
        var removedFromLumis = 0;
        foreach (var agent in _dataStore.Data.Agents)
        {
            if (agent.McpServerIds.Remove(server.Id))
                removedFromLumis++;
        }

        var removedFromChats = RemoveMcpFromChats(server.Name);
        _dataStore.Data.McpServers.Remove(server);

        return Success(
            $"MCP server deleted: {server.Name}. Removed from {removedFromLumis} Lumi(s) and {removedFromChats} chat(s).",
            deletedMcpName: server.Name);
    }

    private string ListMcps(string? query)
    {
        var servers = FilterByQuery(
                _dataStore.Data.McpServers,
                query,
                server => server.Name,
                server => server.Description,
                server => server.Command,
                server => server.Url)
            .OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (servers.Count == 0)
            return string.IsNullOrWhiteSpace(query) ? "No MCP servers found." : $"No MCP servers matched \"{query}\".";

        return "MCP servers:\n" + string.Join("\n", servers.Select(DescribeMcp));
    }

    private FeatureChangeResult CreateMemory(string? key, string? content, string? category)
    {
        var normalizedKey = NormalizeOrNull(key);
        var normalizedContent = NormalizeOrNull(content);
        if (normalizedKey is null)
            return Failure("Memory key is required.");
        if (normalizedContent is null)
            return Failure("Memory content is required.");
        if (_dataStore.Data.Memories.Any(m => string.Equals(m.Key, normalizedKey, StringComparison.OrdinalIgnoreCase)))
            return Failure($"A memory with the key \"{normalizedKey}\" already exists. Use update instead.");

        var memory = new Memory
        {
            Key = normalizedKey,
            Content = normalizedContent,
            Category = NormalizeOrNull(category) ?? "General",
            Source = "manual",
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now
        };

        _dataStore.Data.Memories.Add(memory);
        return Success($"Memory created.\n{DescribeMemory(memory)}");
    }

    private FeatureChangeResult UpdateMemory(string? identifier, string? key, string? content, string? category)
    {
        var lookup = ResolveByIdOrLabel(
            _dataStore.Data.Memories,
            identifier,
            static memory => memory.Id,
            static memory => memory.Key,
            "memory");
        if (!lookup.Success)
            return Failure(lookup.Error!);

        var memory = lookup.Item!;
        if (key is null && content is null && category is null)
            return Failure("No memory changes were provided.");

        if (key is not null)
        {
            var normalizedKey = NormalizeOrNull(key);
            if (normalizedKey is null)
                return Failure("Memory key cannot be empty.");
            if (_dataStore.Data.Memories.Any(m =>
                    m.Id != memory.Id && string.Equals(m.Key, normalizedKey, StringComparison.OrdinalIgnoreCase)))
                return Failure($"A memory with the key \"{normalizedKey}\" already exists.");
            memory.Key = normalizedKey;
        }

        if (content is not null)
        {
            var normalizedContent = NormalizeOrNull(content);
            if (normalizedContent is null)
                return Failure("Memory content cannot be empty.");
            memory.Content = normalizedContent;
        }

        if (category is not null)
            memory.Category = NormalizeOrNull(category) ?? "General";

        memory.Source = "manual";
        memory.UpdatedAt = DateTimeOffset.Now;
        return Success($"Memory updated.\n{DescribeMemory(memory)}");
    }

    private FeatureChangeResult DeleteMemory(string? identifier)
    {
        var lookup = ResolveByIdOrLabel(
            _dataStore.Data.Memories,
            identifier,
            static memory => memory.Id,
            static memory => memory.Key,
            "memory");
        if (!lookup.Success)
            return Failure(lookup.Error!);

        var memory = lookup.Item!;
        _dataStore.Data.Memories.Remove(memory);
        return Success($"Memory deleted: {memory.Key}.");
    }

    private string ListMemories(string? query)
    {
        var memories = FilterByQuery(
                _dataStore.Data.Memories,
                query,
                memory => memory.Key,
                memory => memory.Category,
                memory => memory.Content)
            .OrderBy(memory => memory.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(memory => memory.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (memories.Count == 0)
            return string.IsNullOrWhiteSpace(query) ? "No memories found." : $"No memories matched \"{query}\".";

        return "Memories:\n" + string.Join("\n", memories.Select(DescribeMemory));
    }

    private string DescribeProject(Project project)
    {
        var chatCount = _dataStore.Data.Chats.Count(chat => chat.ProjectId == project.Id);
        var workingDirectory = project.WorkingDirectory ?? "(none)";
        var contextDirectories = project.AdditionalContextDirectories.Count == 0
            ? "(none)"
            : string.Join("; ", project.AdditionalContextDirectories);
        return $"- {project.Id} | {project.Name} | chats: {chatCount} | workdir: {workingDirectory} | context folders: {contextDirectories} | instructions: {Preview(project.Instructions)}";
    }

    private static string DescribeSkill(Skill skill)
    {
        var builtIn = skill.IsBuiltIn ? "built-in" : "custom";
        return $"- {skill.Id} | {skill.IconGlyph} {skill.Name} | {builtIn} | {Preview(skill.Description)}";
    }

    private string DescribeLumi(LumiAgent agent)
    {
        var linkedSkills = ResolveNames(agent.SkillIds, _dataStore.Data.Skills, static skill => skill.Id, static skill => skill.Name);
        var linkedMcps = ResolveNames(agent.McpServerIds, _dataStore.Data.McpServers, static server => server.Id, static server => server.Name);
        var tools = agent.ToolNames.Count == 0 ? "all tools" : string.Join(", ", agent.ToolNames);
        var builtIn = agent.IsBuiltIn ? "built-in" : "custom";
        return $"- {agent.Id} | {agent.IconGlyph} {agent.Name} | {builtIn} | skills: {linkedSkills} | MCPs: {linkedMcps} | tools: {tools} | {Preview(agent.Description)}";
    }

    private static string DescribeMcp(McpServer server)
    {
        var endpoint = server.ServerType == "remote"
            ? server.Url
            : $"{server.Command}{(server.Args.Count > 0 ? $" {string.Join(' ', server.Args)}" : "")}";
        var tools = server.Tools.Count == 0 ? "all tools" : string.Join(", ", server.Tools);
        return $"- {server.Id} | {server.Name} | {server.ServerType} | enabled: {server.IsEnabled} | timeout: {server.Timeout?.ToString() ?? "default"} | tools: {tools} | endpoint: {endpoint} | {Preview(server.Description)}";
    }

    private static string DescribeMemory(Memory memory)
    {
        return $"- {memory.Id} | {memory.Key} | category: {memory.Category} | updated: {memory.UpdatedAt:yyyy-MM-dd HH:mm} | {Preview(memory.Content)}";
    }

    private string DescribeJob(BackgroundJob job)
    {
        lock (job.SyncRoot)
        {
            var chatTitle = _dataStore.Data.Chats.FirstOrDefault(chat => chat.Id == job.ChatId)?.Title ?? "(missing chat)";
            var next = job.NextRunAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "(none)";
            var last = job.LastRunAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "(never)";
            var exit = job.LastScriptExitCode.HasValue ? $" | exit: {job.LastScriptExitCode}" : "";
            return $"- {job.Id} | {job.Name} | {(job.IsEnabled ? "enabled" : "paused")} | {BackgroundJobSchedule.Describe(job)} | chat: {chatTitle} | temporary: {job.IsTemporary} | next: {next} | last: {last} | status: {job.LastRunStatus}{exit} | {Preview(job.Description)}";
        }
    }

    private static string ResolveNames<T>(
        IReadOnlyCollection<Guid> ids,
        IEnumerable<T> items,
        Func<T, Guid> getId,
        Func<T, string> getName)
    {
        if (ids.Count == 0)
            return "(none)";

        var names = items
            .Where(item => ids.Contains(getId(item)))
            .Select(getName)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return names.Count == 0 ? "(none)" : string.Join(", ", names);
    }

    private static string NormalizeAction(string action)
    {
        return NormalizeOrNull(action)?.ToLowerInvariant() switch
        {
            "list" or "show" or "search" => "list",
            "create" or "add" or "new" => "create",
            "update" or "edit" or "rename" or "modify" => "update",
            "delete" or "remove" => "delete",
            _ => ""
        };
    }

    private static string? NormalizeOrNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static List<string> NormalizeList(IEnumerable<string>? values)
    {
        if (values is null)
            return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var value in values)
        {
            var normalized = NormalizeOrNull(value);
            if (normalized is null || !seen.Add(normalized))
                continue;
            result.Add(normalized);
        }

        return result;
    }

    private static IEnumerable<T> FilterByQuery<T>(
        IEnumerable<T> items,
        string? query,
        params Func<T, string?>[] fields)
    {
        var normalizedQuery = NormalizeOrNull(query);
        if (normalizedQuery is null)
            return items;

        return items.Where(item => fields.Any(field =>
            field(item)?.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) == true));
    }

    private static string Preview(string? text, int maxLength = 80)
    {
        var normalized = NormalizeOrNull(text) ?? "(empty)";
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..(maxLength - 1)].TrimEnd() + "…";
    }

    private static FeatureChangeResult InvalidAction(string entityName, string? action)
    {
        return Failure($"Unsupported {entityName} action: {action ?? "(null)"}. Use list, create, update, or delete.");
    }

    private static FeatureChangeResult Failure(string message)
        => new(message);

    private static FeatureChangeResult Success(
        string message,
        bool syncSkillFiles = false,
        string? renamedMcpOldName = null,
        string? renamedMcpNewName = null,
        string? deletedMcpName = null)
        => new(message, DataChanged: true, SyncSkillFiles: syncSkillFiles,
            RenamedMcpOldName: renamedMcpOldName, RenamedMcpNewName: renamedMcpNewName, DeletedMcpName: deletedMcpName);

    private static LookupResult<T> ResolveByIdOrLabel<T>(
        IEnumerable<T> items,
        string? identifier,
        Func<T, Guid> getId,
        Func<T, string> getLabel,
        string entityName)
    {
        var normalizedIdentifier = NormalizeOrNull(identifier);
        if (normalizedIdentifier is null)
            return LookupResult<T>.Fail($"{entityName} identifier is required.");

        if (Guid.TryParse(normalizedIdentifier, out var id))
        {
            var byId = items.FirstOrDefault(item => getId(item) == id);
            return byId is null
                ? LookupResult<T>.Fail($"{entityName} not found: {normalizedIdentifier}")
                : LookupResult<T>.Ok(byId);
        }

        var matches = items
            .Where(item => string.Equals(getLabel(item), normalizedIdentifier, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            0 => LookupResult<T>.Fail($"{entityName} not found: {normalizedIdentifier}"),
            1 => LookupResult<T>.Ok(matches[0]),
            _ => LookupResult<T>.Fail(
                $"Multiple {entityName}s match \"{normalizedIdentifier}\". Use an ID instead.\n"
                + string.Join("\n", matches.Select(item => $"- {getId(item)} | {getLabel(item)}")))
        };
    }

    private LookupManyResult ResolveSkillIds(IEnumerable<string>? identifiers)
    {
        return ResolveIds(
            identifiers,
            _dataStore.Data.Skills,
            static skill => skill.Id,
            static skill => skill.Name,
            "skill");
    }

    private LookupManyResult ResolveMcpIds(IEnumerable<string>? identifiers)
    {
        return ResolveIds(
            identifiers,
            _dataStore.Data.McpServers,
            static server => server.Id,
            static server => server.Name,
            "MCP server");
    }

    private LookupResult<Chat> ResolveChat(string? identifier, Guid? defaultChatId)
    {
        var normalizedIdentifier = NormalizeOrNull(identifier);
        if (normalizedIdentifier is null && defaultChatId.HasValue)
        {
            var defaultChat = _dataStore.Data.Chats.FirstOrDefault(chat => chat.Id == defaultChatId.Value);
            return defaultChat is null
                ? LookupResult<Chat>.Fail($"chat not found: {defaultChatId.Value}")
                : LookupResult<Chat>.Ok(defaultChat);
        }

        return ResolveByIdOrLabel(
            _dataStore.Data.Chats,
            normalizedIdentifier,
            static chat => chat.Id,
            static chat => chat.Title,
            "chat");
    }

    private static bool TrySetRunAt(BackgroundJob job, string? runAt, out string? error)
    {
        error = null;
        if (runAt is null)
            return true;

        var normalizedRunAt = NormalizeOrNull(runAt);
        if (normalizedRunAt is null)
        {
            job.RunAt = null;
            return true;
        }

        if (DateTimeOffset.TryParse(normalizedRunAt, out var parsed))
        {
            job.RunAt = parsed;
            return true;
        }

        if (DateTime.TryParse(normalizedRunAt, out var localDateTime))
        {
            job.RunAt = new DateTimeOffset(localDateTime, TimeZoneInfo.Local.GetUtcOffset(localDateTime));
            return true;
        }

        error = $"Could not parse runAt value \"{runAt}\". Use a local date/time like 2026-04-25 08:00.";
        return false;
    }

    private static LookupManyResult ResolveIds<T>(
        IEnumerable<string>? identifiers,
        IEnumerable<T> items,
        Func<T, Guid> getId,
        Func<T, string> getLabel,
        string entityName)
    {
        if (identifiers is null)
            return LookupManyResult.Ok([]);

        var ids = new List<Guid>();
        var seen = new HashSet<Guid>();
        foreach (var identifier in NormalizeList(identifiers))
        {
            var lookup = ResolveByIdOrLabel(items, identifier, getId, getLabel, entityName);
            if (!lookup.Success)
                return LookupManyResult.Fail(lookup.Error!);

            var id = getId(lookup.Item!);
            if (seen.Add(id))
                ids.Add(id);
        }

        return LookupManyResult.Ok(ids);
    }

    private static bool TryParseKeyValueEntries(
        IEnumerable<string>? entries,
        out Dictionary<string, string>? values,
        out string? error)
    {
        values = [];
        error = null;
        if (entries is null)
            return true;

        foreach (var entry in NormalizeList(entries))
        {
            var separatorIndex = entry.IndexOf('=');
            if (separatorIndex <= 0)
            {
                error = $"Invalid key/value entry: \"{entry}\". Use KEY=VALUE format.";
                values = null;
                return false;
            }

            var key = entry[..separatorIndex].Trim();
            var value = entry[(separatorIndex + 1)..].Trim();
            if (key.Length == 0)
            {
                error = $"Invalid key/value entry: \"{entry}\". The key cannot be empty.";
                values = null;
                return false;
            }

            values[key] = value;
        }

        return true;
    }

    private static bool HasConflictingLabel<T>(
        IEnumerable<T> items,
        string label,
        Func<T, string> getLabel,
        Func<T, Guid> getId,
        Guid? excludeId = null)
    {
        return items.Any(item =>
            (!excludeId.HasValue || getId(item) != excludeId.Value)
            && string.Equals(getLabel(item), label, StringComparison.OrdinalIgnoreCase));
    }

    private static string? NormalizeServerType(string? serverType)
    {
        return NormalizeOrNull(serverType)?.ToLowerInvariant() switch
        {
            null => null,
            "local" or "stdio" => "local",
            "remote" or "sse" or "http" => "remote",
            _ => null
        };
    }

    private int RemoveMcpFromChats(string serverName)
    {
        var removedFromChats = 0;
        foreach (var chat in _dataStore.Data.Chats)
        {
            if (!chat.ActiveMcpServerNames.Remove(serverName))
                continue;

            _dataStore.MarkChatChanged(chat);
            removedFromChats++;
        }

        return removedFromChats;
    }

    private void RenameMcpInChats(string oldName, string newName)
    {
        foreach (var chat in _dataStore.Data.Chats)
        {
            var changed = false;
            for (var i = 0; i < chat.ActiveMcpServerNames.Count; i++)
            {
                if (!string.Equals(chat.ActiveMcpServerNames[i], oldName, StringComparison.Ordinal))
                    continue;

                chat.ActiveMcpServerNames[i] = newName;
                changed = true;
            }

            if (!changed)
                continue;

            chat.ActiveMcpServerNames = chat.ActiveMcpServerNames
                .Distinct(StringComparer.Ordinal)
                .ToList();
            _dataStore.MarkChatChanged(chat);
        }
    }

    private sealed record LookupResult<T>(bool Success, T? Item, string? Error)
    {
        public static LookupResult<T> Ok(T item) => new(true, item, null);
        public static LookupResult<T> Fail(string error) => new(false, default, error);
    }

    private sealed record LookupManyResult(bool Success, List<Guid>? Items, string? Error)
    {
        public static LookupManyResult Ok(List<Guid> items) => new(true, items, null);
        public static LookupManyResult Fail(string error) => new(false, null, error);
    }
}
