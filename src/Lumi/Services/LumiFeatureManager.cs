using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lumi.Localization;
using Lumi.Models;

namespace Lumi.Services;

public sealed record FeatureChangeResult(
    string Message,
    bool DataChanged = false,
    bool SyncSkillFiles = false,
    string? RenamedMcpOldName = null,
    string? RenamedMcpNewName = null,
    string? DeletedMcpName = null,
    int? SkillContentBytes = null,
    string? SkillContentHash = null,
    bool McpCatalogChanged = false,
    bool BackgroundJobsChanged = false,
    bool CapabilityContextChanged = false,
    IReadOnlyList<Guid>? SkillIds = null);

public sealed class LumiFeatureManager
{
    private readonly DataStore _dataStore;
    private readonly bool _localizeOutput;

    public LumiFeatureManager(DataStore dataStore, bool localizeOutput = false)
    {
        _dataStore = dataStore;
        _localizeOutput = localizeOutput;
    }

    private string Text(string key) => _localizeOutput ? Loc.Get(key) : Loc.GetEnglish(key);

    private string Text(string key, params object[] arguments) =>
        string.Format(_localizeOutput ? Loc.Culture : CultureInfo.InvariantCulture, Text(key), arguments);

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
            _ => InvalidAction(Text(nameof(Loc.FeatureManager_ResourceProjects)), action)
        };
    }

    public FeatureChangeResult ManageSkills(
        string action,
        string? identifier = null,
        string? name = null,
        string? description = null,
        string? content = null,
        string? iconGlyph = null,
        string? query = null,
        string? updateMode = null,
        string? editOldString = null,
        string? editNewString = null)
    {
        return NormalizeAction(action) switch
        {
            "list" => ListSkills(query ?? identifier),
            "create" => CreateSkill(name, description, content, iconGlyph, updateMode),
            "update" => UpdateSkill(identifier, name, description, content, iconGlyph, updateMode, editOldString, editNewString),
            "import" => ImportSkill(identifier),
            "delete" => DeleteSkill(identifier),
            _ => InvalidAction(Text(nameof(Loc.FeatureManager_ResourceSkills)), action)
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
            _ => InvalidAction(Text(nameof(Loc.FeatureManager_ResourceAgents)), action)
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
            _ => InvalidAction(Text(nameof(Loc.FeatureManager_ResourceConnectors)), action)
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
            _ => InvalidAction(Text(nameof(Loc.FeatureManager_ResourceMemories)), action)
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
        Guid? defaultChatId = null,
        string? sourceChatIdentifier = null,
        string[]? chatEventTypes = null)
    {
        var normalizedAction = NormalizeOrNull(action)?.ToLowerInvariant() ?? "";
        var result = normalizedAction switch
        {
            "list" or "show" or "search" => new FeatureChangeResult(ListJobs(query ?? identifier)),
            "create" or "add" or "new" => CreateJob(name, description, prompt, chatIdentifier, triggerType, scheduleType,
                intervalMinutes, dailyTime, daysOfWeek, monthlyDay, cronExpression, runAt, scriptContent, scriptLanguage,
                isTemporary, isEnabled, runNow, defaultChatId, sourceChatIdentifier, chatEventTypes),
            "update" or "edit" or "rename" or "modify" => UpdateJob(identifier, name, description, prompt, chatIdentifier,
                triggerType, scheduleType, intervalMinutes, dailyTime, daysOfWeek, monthlyDay, cronExpression, runAt,
                scriptContent, scriptLanguage, isTemporary, isEnabled, runNow, defaultChatId, sourceChatIdentifier,
                chatEventTypes),
            "delete" or "remove" => DeleteJob(identifier),
            "pause" or "disable" => SetJobEnabled(identifier, enabled: false),
            "resume" or "enable" => SetJobEnabled(identifier, enabled: true),
            "run" or "run_now" or "run-now" => RunJobSoon(identifier),
            _ => InvalidAction(Text(nameof(Loc.FeatureManager_ResourceJobs)), action)
        };

        // Keep the next prompt's background-job inventory current without changing session lifecycle.
        return result.DataChanged
            ? result with { BackgroundJobsChanged = true }
            : result;
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
        Guid? defaultChatId,
        string? sourceChatIdentifier,
        string[]? chatEventTypes)
    {
        var normalizedName = NormalizeOrNull(name);
        var normalizedPrompt = NormalizeOrNull(prompt);
        if (normalizedName is null)
            return Failure(Text(nameof(Loc.FeatureManager_JobNameRequired)));
        if (normalizedPrompt is null)
            return Failure(Text(nameof(Loc.FeatureManager_JobPromptRequired)));
        var jobs = _dataStore.SnapshotBackgroundJobs();
        if (HasConflictingLabel(jobs, normalizedName, static job => job.Name, static job => job.Id))
            return Failure(Text("FeatureManager_JobDuplicateName", normalizedName));

        var chatLookup = ResolveChat(chatIdentifier, defaultChatId);
        if (!chatLookup.Success)
            return Failure(chatLookup.Error!);

        var normalizedTriggerType = triggerType is null && !string.IsNullOrWhiteSpace(scriptContent)
            ? BackgroundJobTriggerTypes.Script
            : BackgroundJobSchedule.NormalizeTriggerType(triggerType);
        Guid? sourceChatId = null;
        List<string> normalizedChatEventTypes = [];
        if (normalizedTriggerType == BackgroundJobTriggerTypes.ChatEvent)
        {
            var sourceChatLookup = ResolveChat(sourceChatIdentifier, defaultChatId: null);
            if (!sourceChatLookup.Success)
                return Failure(Text("FeatureManager_SourceError", sourceChatLookup.Error!));
            if (sourceChatLookup.Item!.Id == chatLookup.Item!.Id)
                return Failure(Text(nameof(Loc.FeatureManager_JobDifferentChatRequired)));
            if (BackgroundJobSchedule.WouldCreateChatEventCycle(
                    jobs,
                    sourceChatLookup.Item.Id,
                    chatLookup.Item.Id))
            {
                return Failure(Text(nameof(Loc.FeatureManager_JobTriggerCycle)));
            }
            if (!ChatLifecycleEventTypes.TryNormalize(
                    chatEventTypes,
                    out normalizedChatEventTypes,
                    out var eventTypesError,
                    defaultToIdle: true,
                    localizeError: _localizeOutput))
            {
                return Failure(eventTypesError);
            }

            sourceChatId = sourceChatLookup.Item.Id;
        }

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
            ScriptLanguage = NormalizeOrNull(scriptLanguage) ?? BackgroundJobScriptLanguages.DefaultForCurrentOs(),
            SourceChatId = sourceChatId,
            ChatEventTypes = normalizedChatEventTypes,
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
        return Success(Text("FeatureManager_JobCreated", DescribeJob(job)));
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
        Guid? defaultChatId,
        string? sourceChatIdentifier,
        string[]? chatEventTypes)
    {
        var lookup = ResolveByIdOrLabel(
            _dataStore.SnapshotBackgroundJobs(),
            identifier,
            static job => job.Id,
            static job => job.Name,
            Text(nameof(Loc.FeatureManager_EntityJob)));
        if (!lookup.Success)
            return Failure(lookup.Error!);

        if (name is null && description is null && prompt is null && chatIdentifier is null
            && triggerType is null && scheduleType is null && intervalMinutes is null && dailyTime is null
            && daysOfWeek is null && monthlyDay is null && cronExpression is null && runAt is null
            && scriptContent is null && scriptLanguage is null
            && isTemporary is null && isEnabled is null && runNow is null
            && sourceChatIdentifier is null && chatEventTypes is null)
            return Failure(Text(nameof(Loc.FeatureManager_JobNoChanges)));

        var job = lookup.Item!;
        lock (job.SyncRoot)
        {
            var candidate = CreateJobUpdateCandidate(job);
            var proposedTriggerType = triggerType is null
                ? BackgroundJobSchedule.NormalizeTriggerType(candidate.TriggerType)
                : BackgroundJobSchedule.NormalizeTriggerType(triggerType);
            var proposedTargetChatId = candidate.ChatId;
            if (chatIdentifier is not null)
            {
                var chatLookup = ResolveChat(chatIdentifier, defaultChatId);
                if (!chatLookup.Success)
                    return Failure(chatLookup.Error!);
                proposedTargetChatId = chatLookup.Item!.Id;
            }

            var proposedSourceChatId = candidate.SourceChatId;
            if (sourceChatIdentifier is not null)
            {
                var sourceChatLookup = ResolveChat(sourceChatIdentifier, defaultChatId: null);
                if (!sourceChatLookup.Success)
                    return Failure(Text("FeatureManager_SourceError", sourceChatLookup.Error!));
                proposedSourceChatId = sourceChatLookup.Item!.Id;
            }

            var proposedChatEventTypes = candidate.ChatEventTypes.ToList();
            if (proposedTriggerType == BackgroundJobTriggerTypes.ChatEvent)
            {
                if (proposedSourceChatId is null)
                    return Failure(Text(nameof(Loc.FeatureManager_JobSourceRequired)));
                if (proposedSourceChatId == proposedTargetChatId)
                    return Failure(Text(nameof(Loc.FeatureManager_JobDifferentChatRequired)));
                if (BackgroundJobSchedule.WouldCreateChatEventCycle(
                        _dataStore.SnapshotBackgroundJobs(),
                        proposedSourceChatId.Value,
                        proposedTargetChatId,
                        excludedJobId: job.Id))
                {
                    return Failure(Text(nameof(Loc.FeatureManager_JobTriggerCycle)));
                }
                if (!ChatLifecycleEventTypes.TryNormalize(
                        chatEventTypes is null ? proposedChatEventTypes : chatEventTypes,
                        out proposedChatEventTypes,
                        out var eventTypesError,
                        defaultToIdle: true,
                        localizeError: _localizeOutput))
                {
                    return Failure(eventTypesError);
                }
            }
            else
            {
                proposedSourceChatId = null;
                proposedChatEventTypes = [];
            }

            if (name is not null)
            {
                var normalizedName = NormalizeOrNull(name);
                if (normalizedName is null)
                    return Failure(Text(nameof(Loc.FeatureManager_JobNameEmpty)));
                if (HasConflictingLabel(_dataStore.SnapshotBackgroundJobs(), normalizedName, static item => item.Name, static item => item.Id, job.Id))
                    return Failure(Text("FeatureManager_JobDuplicateName", normalizedName));
                candidate.Name = normalizedName;
            }

            if (description is not null)
                candidate.Description = NormalizeOrNull(description) ?? "";

            if (prompt is not null)
            {
                var normalizedPrompt = NormalizeOrNull(prompt);
                if (normalizedPrompt is null)
                    return Failure(Text(nameof(Loc.FeatureManager_JobPromptEmpty)));
                candidate.Prompt = normalizedPrompt;
            }

            candidate.ChatId = proposedTargetChatId;
            candidate.TriggerType = proposedTriggerType;
            if (scheduleType is not null)
                candidate.ScheduleType = scheduleType;
            if (intervalMinutes.HasValue)
                candidate.IntervalMinutes = intervalMinutes.Value;
            if (dailyTime is not null)
                candidate.DailyTime = NormalizeOrNull(dailyTime) ?? "08:00";
            if (daysOfWeek is not null)
                candidate.DaysOfWeek = NormalizeOrNull(daysOfWeek) ?? "Mon,Tue,Wed,Thu,Fri";
            if (monthlyDay.HasValue)
                candidate.MonthlyDay = monthlyDay.Value;
            if (cronExpression is not null)
                candidate.CronExpression = NormalizeOrNull(cronExpression) ?? "0 8 * * *";
            if (scriptContent is not null)
                candidate.ScriptContent = NormalizeOrNull(scriptContent) ?? "";
            if (scriptLanguage is not null)
                candidate.ScriptLanguage = NormalizeOrNull(scriptLanguage) ?? BackgroundJobScriptLanguages.DefaultForCurrentOs();
            candidate.SourceChatId = proposedSourceChatId;
            candidate.ChatEventTypes = proposedChatEventTypes;
            if (isTemporary.HasValue)
                candidate.IsTemporary = isTemporary.Value;
            if (isEnabled.HasValue)
                candidate.IsEnabled = isEnabled.Value;

            if (!TrySetRunAt(candidate, runAt, out var runAtError))
                return Failure(runAtError!);

            if (!TryValidateJobConfiguration(candidate, out var configurationError))
                return Failure(configurationError!);
            BackgroundJobSchedule.Normalize(candidate);
            if (candidate.TriggerType == BackgroundJobTriggerTypes.Script)
                candidate.IsTemporary = true;

            var now = DateTimeOffset.Now;
            candidate.NextRunAt = candidate.IsEnabled
                ? candidate.TriggerType == BackgroundJobTriggerTypes.Script || runNow == true
                    ? now
                    : BackgroundJobSchedule.ComputeNextRun(candidate, now, afterRun: false)
                : null;
            candidate.UpdatedAt = now;
            ApplyJobUpdateCandidate(job, candidate);
        }

        _dataStore.MarkBackgroundJobsChanged();

        return Success(Text("FeatureManager_JobUpdated", DescribeJob(job)));
    }

    private static BackgroundJob CreateJobUpdateCandidate(BackgroundJob source)
    {
        return new BackgroundJob
        {
            Id = source.Id,
            ChatId = source.ChatId,
            Name = source.Name,
            Description = source.Description,
            Prompt = source.Prompt,
            TriggerType = source.TriggerType,
            ScheduleType = source.ScheduleType,
            IntervalMinutes = source.IntervalMinutes,
            DailyTime = source.DailyTime,
            DaysOfWeek = source.DaysOfWeek,
            MonthlyDay = source.MonthlyDay,
            CronExpression = source.CronExpression,
            RunAt = source.RunAt,
            ScriptContent = source.ScriptContent,
            ScriptLanguage = source.ScriptLanguage,
            SourceChatId = source.SourceChatId,
            ChatEventTypes = [.. source.ChatEventTypes],
            IsEnabled = source.IsEnabled,
            IsTemporary = source.IsTemporary,
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt,
            LastRunAt = source.LastRunAt,
            NextRunAt = source.NextRunAt
        };
    }

    private static void ApplyJobUpdateCandidate(BackgroundJob target, BackgroundJob candidate)
    {
        target.ChatId = candidate.ChatId;
        target.Name = candidate.Name;
        target.Description = candidate.Description;
        target.Prompt = candidate.Prompt;
        target.TriggerType = candidate.TriggerType;
        target.ScheduleType = candidate.ScheduleType;
        target.IntervalMinutes = candidate.IntervalMinutes;
        target.DailyTime = candidate.DailyTime;
        target.DaysOfWeek = candidate.DaysOfWeek;
        target.MonthlyDay = candidate.MonthlyDay;
        target.CronExpression = candidate.CronExpression;
        target.RunAt = candidate.RunAt;
        target.ScriptContent = candidate.ScriptContent;
        target.ScriptLanguage = candidate.ScriptLanguage;
        target.SourceChatId = candidate.SourceChatId;
        target.ChatEventTypes = [.. candidate.ChatEventTypes];
        target.IsEnabled = candidate.IsEnabled;
        target.IsTemporary = candidate.IsTemporary;
        target.NextRunAt = candidate.NextRunAt;
        target.UpdatedAt = candidate.UpdatedAt;
        target.MarkConfigurationChanged();
    }

    private bool TryValidateJobConfiguration(BackgroundJob job, out string? error)
    {
        error = null;
        var triggerType = BackgroundJobSchedule.NormalizeTriggerType(job.TriggerType);
        var scheduleType = BackgroundJobSchedule.NormalizeScheduleType(job.ScheduleType);

        if (triggerType == BackgroundJobTriggerTypes.Script)
        {
            if (string.IsNullOrWhiteSpace(job.ScriptContent))
            {
                error = Text(nameof(Loc.FeatureManager_JobScriptRequired));
                return false;
            }

            return true;
        }

        if (triggerType == BackgroundJobTriggerTypes.ChatEvent)
        {
            if (job.SourceChatId is null)
            {
                error = Text(nameof(Loc.FeatureManager_JobSourceRequired));
                return false;
            }

            if (job.SourceChatId == job.ChatId)
            {
                error = Text(nameof(Loc.FeatureManager_JobDifferentChatRequired));
                return false;
            }

            if (!ChatLifecycleEventTypes.TryNormalize(
                    job.ChatEventTypes,
                    out var normalizedEventTypes,
                    out error,
                    defaultToIdle: true,
                    localizeError: _localizeOutput))
            {
                return false;
            }

            job.ChatEventTypes = normalizedEventTypes;
            return true;
        }

        if ((scheduleType == BackgroundJobScheduleTypes.Daily
             || scheduleType == BackgroundJobScheduleTypes.Weekly
             || scheduleType == BackgroundJobScheduleTypes.Monthly)
            && !BackgroundJobSchedule.TryValidateDailyTime(job.DailyTime, out var timeError, localize: _localizeOutput))
        {
            error = timeError;
            return false;
        }

        if (scheduleType == BackgroundJobScheduleTypes.Weekly
            && !BackgroundJobSchedule.TryValidateDaysOfWeek(job.DaysOfWeek, out var daysError, localize: _localizeOutput))
        {
            error = daysError;
            return false;
        }

        if (scheduleType == BackgroundJobScheduleTypes.Monthly
            && (job.MonthlyDay < 1 || job.MonthlyDay > 31))
        {
            error = Text(nameof(Loc.Jobs_ValidationMonthlyDay));
            return false;
        }

        if (scheduleType == BackgroundJobScheduleTypes.Cron
            && !BackgroundJobSchedule.TryValidateCronExpression(job.CronExpression, out var cronError, localize: _localizeOutput))
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
            Text(nameof(Loc.FeatureManager_EntityJob)));
        if (!lookup.Success)
            return Failure(lookup.Error!);

        var job = lookup.Item!;
        _dataStore.RemoveBackgroundJob(job);
        return Success(Text("FeatureManager_JobDeleted", job.Name));
    }

    private FeatureChangeResult SetJobEnabled(string? identifier, bool enabled)
    {
        var lookup = ResolveByIdOrLabel(
            _dataStore.SnapshotBackgroundJobs(),
            identifier,
            static job => job.Id,
            static job => job.Name,
            Text(nameof(Loc.FeatureManager_EntityJob)));
        if (!lookup.Success)
            return Failure(lookup.Error!);

        var job = lookup.Item!;
        if (enabled
            && job.TriggerType == BackgroundJobTriggerTypes.ChatEvent
            && job.SourceChatId is { } sourceChatId
            && BackgroundJobSchedule.WouldCreateChatEventCycle(
                _dataStore.SnapshotBackgroundJobs(),
                sourceChatId,
                job.ChatId,
                excludedJobId: job.Id))
        {
            return Failure(Text(nameof(Loc.FeatureManager_JobTriggerCycle)));
        }

        lock (job.SyncRoot)
        {
            job.IsEnabled = enabled;
            job.NextRunAt = enabled ? BackgroundJobSchedule.ComputeNextRun(job, DateTimeOffset.Now, afterRun: false) : null;
            job.UpdatedAt = DateTimeOffset.Now;
            job.MarkConfigurationChanged();
        }

        _dataStore.MarkBackgroundJobsChanged();
        return Success(Text(
            enabled ? "FeatureManager_JobResumed" : "FeatureManager_JobPaused",
            DescribeJob(job)));
    }

    private FeatureChangeResult RunJobSoon(string? identifier)
    {
        var lookup = ResolveByIdOrLabel(
            _dataStore.SnapshotBackgroundJobs(),
            identifier,
            static job => job.Id,
            static job => job.Name,
            Text(nameof(Loc.FeatureManager_EntityJob)));
        if (!lookup.Success)
            return Failure(lookup.Error!);

        var job = lookup.Item!;
        lock (job.SyncRoot)
        {
            job.IsEnabled = true;
            job.NextRunAt = DateTimeOffset.Now;
            job.UpdatedAt = DateTimeOffset.Now;
            job.MarkConfigurationChanged();
        }

        _dataStore.MarkBackgroundJobsChanged();
        return Success(Text("FeatureManager_JobQueued", DescribeJob(job)));
    }

    private string ListJobs(string? query)
    {
        var jobs = FilterByQuery(
                _dataStore.SnapshotBackgroundJobs(),
                query,
                job => job.Name,
                job => job.Description,
                job => job.Prompt,
                job => job.LastRunSummary,
                job => string.Join(" ", job.ChatEventTypes),
                job => job.SourceChatId is { } sourceChatId
                    ? _dataStore.Data.Chats.FirstOrDefault(chat => chat.Id == sourceChatId)?.Title
                    : null)
            .OrderBy(job => job.NextRunAt ?? DateTimeOffset.MaxValue)
            .ThenBy(job => job.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (jobs.Count == 0)
            return string.IsNullOrWhiteSpace(query)
                ? Text(nameof(Loc.FeatureManager_JobsNone))
                : Text("FeatureManager_JobsNoMatch", query);

        return Text("FeatureManager_JobsList", string.Join("\n", jobs.Select(DescribeJob)));
    }

    private FeatureChangeResult CreateProject(
        string? name,
        string? instructions,
        string? workingDirectory,
        string[]? additionalContextDirectories)
    {
        var normalizedName = NormalizeOrNull(name);
        if (normalizedName is null)
            return Failure(Text(nameof(Loc.FeatureManager_ProjectNameRequired)));
        if (HasConflictingLabel(_dataStore.Data.Projects, normalizedName, static project => project.Name, static project => project.Id))
            return Failure(Text("FeatureManager_ProjectDuplicateName", normalizedName));

        var project = new Project
        {
            Name = normalizedName,
            Instructions = NormalizeOrNull(instructions) ?? "",
            WorkingDirectory = NormalizeOrNull(workingDirectory),
            AdditionalContextDirectories = ProjectContextDirectoryHelper.NormalizeFolderList(additionalContextDirectories)
        };

        _dataStore.Data.Projects.Add(project);
        return Success(Text("FeatureManager_ProjectCreated", project.Name));
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
            Text(nameof(Loc.FeatureManager_EntityProject)));
        if (!lookup.Success)
            return Failure(lookup.Error!);

        var project = lookup.Item!;
        if (name is null
            && instructions is null
            && workingDirectory is null
            && clearWorkingDirectory != true
            && additionalContextDirectories is null
            && clearAdditionalContextDirectories != true)
            return Failure(Text(nameof(Loc.FeatureManager_ProjectNoChanges)));

        if (name is not null)
        {
            var normalizedName = NormalizeOrNull(name);
            if (normalizedName is null)
                return Failure(Text(nameof(Loc.FeatureManager_ProjectNameEmpty)));
            if (HasConflictingLabel(_dataStore.Data.Projects, normalizedName, static item => item.Name, static item => item.Id, project.Id))
                return Failure(Text("FeatureManager_ProjectDuplicateName", normalizedName));
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

        return Success(Text("FeatureManager_ProjectUpdated", DescribeProject(project)));
    }

    private FeatureChangeResult DeleteProject(string? identifier)
    {
        var lookup = ResolveByIdOrLabel(
            _dataStore.Data.Projects,
            identifier,
            static project => project.Id,
            static project => project.Name,
            Text(nameof(Loc.FeatureManager_EntityProject)));
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
        return Success(Text("FeatureManager_ProjectDeleted", project.Name, affectedChats));
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
            return string.IsNullOrWhiteSpace(query)
                ? Text(nameof(Loc.FeatureManager_ProjectsNone))
                : Text("FeatureManager_ProjectsNoMatch", query);

        return Text("FeatureManager_ProjectsList", string.Join("\n", projects.Select(DescribeProject)));
    }

    private const int SkillOversizeWarningBytes = 12_288; // 12 KB

    private FeatureChangeResult CreateSkill(string? name, string? description, string? content, string? iconGlyph, string? updateMode)
    {
        var mode = NormalizeUpdateMode(updateMode);
        if (mode is null)
            return Failure(Text("FeatureManager_UpdateModeUnknown", updateMode!));
        if (mode != "replace")
            return Failure(Text("FeatureManager_UpdateModeRequiresSkill", mode));

        var normalizedName = NormalizeOrNull(name);
        var normalizedContent = NormalizeOrNull(content);
        if (normalizedName is null)
            return Failure(Text(nameof(Loc.FeatureManager_SkillNameRequired)));
        if (normalizedContent is null)
            return Failure(Text(nameof(Loc.FeatureManager_SkillContentRequired)));
        if (HasConflictingLabel(_dataStore.Data.Skills, normalizedName, static skill => skill.Name, static skill => skill.Id))
            return Failure(Text("FeatureManager_SkillDuplicateName", normalizedName));
        if (ValidateSkillMirrorScalars(normalizedName, NormalizeOrNull(description)) is { } scalarError)
            return Failure(scalarError);
        if (_dataStore.SkillFileNameConflicts(normalizedName))
            return Failure(Text("FeatureManager_SkillMirrorConflict", normalizedName));

        var skill = new Skill
        {
            Name = normalizedName,
            Description = NormalizeOrNull(description) ?? "",
            Content = normalizedContent,
            IconGlyph = NormalizeOrNull(iconGlyph) ?? "⚡"
        };

        _dataStore.Data.Skills.Add(skill);
        return SkillSuccess(skill, Text(nameof(Loc.FeatureManager_SkillCreated)));
    }

    private FeatureChangeResult UpdateSkill(
        string? identifier,
        string? name,
        string? description,
        string? content,
        string? iconGlyph,
        string? updateMode,
        string? editOldString,
        string? editNewString)
    {
        var lookup = ResolveByIdOrLabel(
            _dataStore.Data.Skills,
            identifier,
            static skill => skill.Id,
            static skill => skill.Name,
            Text(nameof(Loc.FeatureManager_EntitySkill)));
        if (!lookup.Success)
            return Failure(lookup.Error!);

        var skill = lookup.Item!;

        var mode = NormalizeUpdateMode(updateMode);
        if (mode is null)
            return Failure(Text("FeatureManager_UpdateModeUnknown", updateMode!));

        var contentEditRequested = mode != "replace" || content is not null;
        var metadataChange = name is not null || description is not null || iconGlyph is not null;
        if (!contentEditRequested && !metadataChange)
            return Failure(Text(nameof(Loc.FeatureManager_SkillNoChanges)));

        // Stage and validate every requested change before mutating the skill, so a failed
        // content edit can never leave a half-applied rename or metadata change behind.
        string? stagedName = null;
        if (name is not null)
        {
            stagedName = NormalizeOrNull(name);
            if (stagedName is null)
                return Failure(Text(nameof(Loc.FeatureManager_SkillNameEmpty)));
            if (HasConflictingLabel(_dataStore.Data.Skills, stagedName, static item => item.Name, static item => item.Id, skill.Id))
                return Failure(Text("FeatureManager_SkillDuplicateName", stagedName));
            if (_dataStore.SkillFileNameConflicts(stagedName, skill.Id))
                return Failure(Text("FeatureManager_SkillMirrorConflict", stagedName));
        }

        var stagedDescription = description is not null ? NormalizeOrNull(description) ?? "" : null;
        if (ValidateSkillMirrorScalars(stagedName, stagedDescription) is { } scalarError)
            return Failure(scalarError);

        string? stagedContent = null;
        if (contentEditRequested)
        {
            var (ok, newContent, error) = ComputeEditedContent(mode, skill.Content, content, editOldString, editNewString);
            if (!ok)
                return Failure(error!);
            if (string.IsNullOrWhiteSpace(newContent))
                return Failure(Text(nameof(Loc.FeatureManager_SkillContentEmpty)));
            stagedContent = newContent;
        }

        // Back up the current content BEFORE mutating any field, so a backup I/O failure aborts
        // the whole update with the skill left untouched (atomicity). Then commit metadata +
        // content together. Metadata first so a rename + content edit in one call still yields
        // the correct .md filename.
        if (stagedContent is not null && TryBackupSkillContent(skill, stagedContent) is { } backupError)
            return Failure(backupError);

        if (stagedName is not null)
            skill.Name = stagedName;
        if (stagedDescription is not null)
            skill.Description = stagedDescription;
        if (iconGlyph is not null)
            skill.IconGlyph = NormalizeOrNull(iconGlyph) ?? "⚡";
        if (stagedContent is not null)
            skill.Content = stagedContent;

        return SkillSuccess(skill, Text(nameof(Loc.FeatureManager_SkillUpdated)));
    }

    private FeatureChangeResult ImportSkill(string? identifier)
    {
        var lookup = ResolveByIdOrLabel(
            _dataStore.Data.Skills,
            identifier,
            static skill => skill.Id,
            static skill => skill.Name,
            Text(nameof(Loc.FeatureManager_EntitySkill)));
        if (!lookup.Success)
            return Failure(lookup.Error!);

        var skill = lookup.Item!;
        var path = _dataStore.GetSkillFilePath(skill.Name);
        if (!File.Exists(path))
            return Failure(Text("FeatureManager_SkillFileNotFound", path));

        string raw;
        try
        {
            raw = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            return Failure(Text("FeatureManager_SkillReadFailed", ex.Message));
        }

        var (ok, importedName, importedDescription, body, error) = ParseSkillMarkdown(raw);
        if (!ok)
            return Failure(error!);
        if (string.IsNullOrWhiteSpace(body))
            return Failure(Text(nameof(Loc.FeatureManager_SkillImportBodyEmpty)));

        var newName = NormalizeOrNull(importedName);
        if (newName is null)
            return Failure(Text(nameof(Loc.FeatureManager_SkillImportNameEmpty)));
        var newDescription = NormalizeOrNull(importedDescription) ?? "";

        if (!string.Equals(newName, skill.Name, StringComparison.Ordinal)
            && HasConflictingLabel(_dataStore.Data.Skills, newName, static item => item.Name, static item => item.Id, skill.Id))
        {
            return Failure(Text("FeatureManager_SkillDuplicateName", newName));
        }
        if (ValidateSkillMirrorScalars(newName, newDescription) is { } importScalarError)
            return Failure(importScalarError);
        if (_dataStore.SkillFileNameConflicts(newName, skill.Id))
            return Failure(Text("FeatureManager_SkillImportMirrorConflict", newName));

        // Back up before mutating anything, so a backup failure leaves the skill untouched.
        if (TryBackupSkillContent(skill, body!) is { } importBackupError)
            return Failure(importBackupError);

        skill.Name = newName;
        skill.Description = newDescription;
        skill.Content = body!;
        return SkillSuccess(skill, Text("FeatureManager_SkillImported", Path.GetFileName(path)));
    }

    /// <summary>Backs up the skill's current content before it is overwritten. Returns an error
    /// message if the backup fails, so the caller can abort without having mutated the skill.</summary>
    private string? TryBackupSkillContent(Skill skill, string newContent)
    {
        if (string.IsNullOrEmpty(skill.Content)
            || string.Equals(skill.Content, newContent, StringComparison.Ordinal))
            return null;

        try
        {
            _dataStore.BackupSkillContent(skill.Id, skill.Content);
            return null;
        }
        catch (IOException ex)
        {
            return Text("FeatureManager_SkillBackupFailed", ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Text("FeatureManager_SkillBackupFailed", ex.Message);
        }
    }

    /// <summary>Rejects skill name/description values that would corrupt the single-line YAML
    /// frontmatter in the on-disk mirror (line breaks, or a leading frontmatter delimiter).</summary>
    private string? ValidateSkillMirrorScalars(string? name, string? description)
    {
        if (name is not null && IsMirrorUnsafeScalar(name))
            return Text(nameof(Loc.FeatureManager_SkillNameInvalidScalar));
        if (description is not null && IsMirrorUnsafeScalar(description))
            return Text(nameof(Loc.FeatureManager_SkillDescriptionInvalidScalar));
        return null;
    }

    private static bool IsMirrorUnsafeScalar(string value)
        => value.IndexOf('\n') >= 0
            || value.IndexOf('\r') >= 0
            || value.TrimStart().StartsWith("---", StringComparison.Ordinal);

    private FeatureChangeResult SkillSuccess(Skill skill, string headline)
    {
        var bytes = Encoding.UTF8.GetByteCount(skill.Content);
        var hash = ShortHash(skill.Content);

        var message = new StringBuilder();
        message.Append(headline).Append('\n').Append(DescribeSkill(skill));
        message.Append(Text("FeatureManager_SkillVerified", bytes, hash));
        if (bytes > SkillOversizeWarningBytes)
            message.Append(Text("FeatureManager_SkillSizeWarning", FormatKb(bytes)));

        return Success(message.ToString(), syncSkillFiles: true, skillContentBytes: bytes, skillContentHash: hash)
            with { SkillIds = [skill.Id] };
    }

    private (bool Ok, string? Content, string? Error) ComputeEditedContent(
        string mode,
        string currentContent,
        string? content,
        string? editOldString,
        string? editNewString)
    {
        switch (mode)
        {
            case "replace":
                var normalized = NormalizeOrNull(content);
                return normalized is null
                    ? (false, null, Text(nameof(Loc.FeatureManager_SkillContentEmpty)))
                    : (true, normalized, null);
            case "patch":
                return ApplyPatch(currentContent, editOldString, editNewString);
            case "append":
                return string.IsNullOrEmpty(editNewString)
                    ? (false, null, Text("FeatureManager_EditNewStringRequired", "append"))
                    : (true, currentContent + "\n" + editNewString, null);
            case "prepend":
                return string.IsNullOrEmpty(editNewString)
                    ? (false, null, Text("FeatureManager_EditNewStringRequired", "prepend"))
                    : (true, editNewString + "\n" + currentContent, null);
            case "replacesection":
                return ApplyReplaceSection(currentContent, editOldString, editNewString);
            default:
                return (false, null, Text("FeatureManager_UpdateModeUnsupported", mode));
        }
    }

    private (bool Ok, string? Content, string? Error) ApplyPatch(string content, string? editOldString, string? editNewString)
    {
        if (string.IsNullOrEmpty(editOldString))
            return (false, null, Text(nameof(Loc.FeatureManager_PatchOldStringRequired)));

        var replacement = editNewString ?? "";
        var count = CountOccurrences(content, editOldString);
        if (count == 0)
            return (false, null, Text(nameof(Loc.FeatureManager_PatchNotFound)));
        if (count > 1)
            return (false, null, Text("FeatureManager_PatchAmbiguous", count));

        var idx = content.IndexOf(editOldString, StringComparison.Ordinal);
        var result = string.Concat(content.AsSpan(0, idx), replacement, content.AsSpan(idx + editOldString.Length));
        return (true, result, null);
    }

    private (bool Ok, string? Content, string? Error) ApplyReplaceSection(string content, string? headingLine, string? editNewString)
    {
        var heading = NormalizeOrNull(headingLine);
        if (heading is null)
            return (false, null, Text(nameof(Loc.FeatureManager_SectionHeadingRequired)));

        var level = HeadingLevel(heading);
        if (level <= 0)
            return (false, null, Text("FeatureManager_SectionHeadingInvalid", heading));

        var lines = content.Split('\n');
        var inCode = ComputeFencedCodeMask(lines);
        var start = -1;
        var matches = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            if (inCode[i])
                continue;
            if (string.Equals(lines[i].Trim(), heading, StringComparison.Ordinal))
            {
                matches++;
                if (start < 0)
                    start = i;
            }
        }
        if (start < 0)
            return (false, null, Text("FeatureManager_SectionHeadingNotFound", heading));
        if (matches > 1)
            return (false, null, Text("FeatureManager_SectionHeadingAmbiguous", heading, matches));

        var end = lines.Length;
        for (var j = start + 1; j < lines.Length; j++)
        {
            if (inCode[j])
                continue;
            var lvl = HeadingLevel(lines[j].Trim());
            if (lvl > 0 && lvl <= level)
            {
                end = j;
                break;
            }
        }

        var rebuilt = new List<string>(lines.Length);
        rebuilt.AddRange(lines[..start]);
        rebuilt.Add(editNewString ?? "");
        rebuilt.AddRange(lines[end..]);
        return (true, string.Join('\n', rebuilt), null);
    }

    private static int HeadingLevel(string line)
    {
        var trimmed = line.TrimStart();
        var hashes = 0;
        while (hashes < trimmed.Length && trimmed[hashes] == '#')
            hashes++;
        if (hashes == 0)
            return 0;
        // A valid ATX heading has a space (or line end) after the leading hashes.
        if (hashes < trimmed.Length && trimmed[hashes] != ' ')
            return 0;
        return hashes;
    }

    /// <summary>Marks each line that is inside (or is the delimiter of) a fenced code block, so
    /// heading-looking lines within ``` / ~~~ fences aren't mistaken for real section headings.</summary>
    private static bool[] ComputeFencedCodeMask(string[] lines)
    {
        var mask = new bool[lines.Length];
        var inFence = false;
        var fenceChar = '\0';
        var fenceLen = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var delim = FenceDelimiter(lines[i]);
            if (!inFence)
            {
                if (delim is { } open)
                {
                    inFence = true;
                    fenceChar = open.Ch;
                    fenceLen = open.Len;
                    mask[i] = true;
                }
            }
            else
            {
                mask[i] = true; // interior line, including the closing delimiter
                // Closing fence: same char, at least as long, and only fence chars (no info string).
                if (delim is { } close && close.Ch == fenceChar && close.Len >= fenceLen
                    && lines[i].Trim().Length == close.Len)
                {
                    inFence = false;
                }
            }
        }
        return mask;
    }

    /// <summary>Returns the fence character and run length when a line opens/closes a code fence
    /// (3+ backticks or tildes), else null.</summary>
    private static (char Ch, int Len)? FenceDelimiter(string line)
    {
        var indent = 0;
        while (indent < line.Length && line[indent] == ' ')
            indent++;
        if (indent > 3 || (indent < line.Length && line[indent] == '\t'))
            return null;

        var trimmed = line[indent..];
        if (trimmed.Length < 3)
            return null;
        var ch = trimmed[0];
        if (ch != '`' && ch != '~')
            return null;
        var len = 0;
        while (len < trimmed.Length && trimmed[len] == ch)
            len++;
        return len >= 3 ? (ch, len) : null;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            // Advance by one (not needle.Length) so overlapping matches are all counted — e.g.
            // "aa" occurs at index 0 and 1 in "aaa". A patch must treat that as ambiguous, not unique.
            i += 1;
        }
        return count;
    }

    private (bool Ok, string? Name, string? Description, string? Body, string? Error) ParseSkillMarkdown(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return (false, null, null, null, Text(nameof(Loc.FeatureManager_FrontmatterEmpty)));

        var (first, firstNext) = ReadLine(raw, 0);
        if (first.Trim() != "---")
            return (false, null, null, null, Text(nameof(Loc.FeatureManager_FrontmatterOpeningRequired)));

        var pos = firstNext;
        string? name = null;
        string? description = null;
        var closed = false;
        while (pos < raw.Length)
        {
            var (line, next) = ReadLine(raw, pos);
            pos = next;
            if (line.Trim() == "---")
            {
                closed = true;
                break;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            var key = line[..colon].Trim();
            string value;
            try
            {
                value = DataStore.DecodeYamlScalar(line[(colon + 1)..].Trim());
            }
            catch (JsonException ex)
            {
                return (false, null, null, null,
                    Text("FeatureManager_FrontmatterQuotedValueInvalid", key, ex.Message));
            }
            if (key.Equals("name", StringComparison.OrdinalIgnoreCase))
                name = value;
            else if (key.Equals("description", StringComparison.OrdinalIgnoreCase))
                description = value;
        }

        if (!closed)
            return (false, null, null, null, Text(nameof(Loc.FeatureManager_FrontmatterClosingRequired)));
        if (string.IsNullOrWhiteSpace(name))
            return (false, null, null, null, Text(nameof(Loc.FeatureManager_FrontmatterNameRequired)));

        // Skip exactly one blank separator line between frontmatter and body (matches the mirror layout).
        if (pos < raw.Length)
        {
            var (sep, next) = ReadLine(raw, pos);
            if (sep.Length == 0)
                pos = next;
        }

        var body = pos <= raw.Length ? raw[pos..] : "";
        return (true, name, description, body, null);
    }

    // Returns the line WITHOUT its trailing newline, and the index just past the newline.
    private static (string Line, int Next) ReadLine(string text, int start)
    {
        var nl = text.IndexOf('\n', start);
        if (nl < 0)
            return (text[start..], text.Length);
        var line = text[start..nl];
        if (line.EndsWith('\r'))
            line = line[..^1];
        return (line, nl + 1);
    }

    private static string FormatKb(int bytes)
        => (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";

    private static string ShortHash(string content)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    private static string? NormalizeUpdateMode(string? updateMode)
    {
        var normalized = NormalizeOrNull(updateMode)?.ToLowerInvariant().Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        return normalized switch
        {
            null or "replace" or "full" or "set" => "replace",
            "patch" or "strreplace" => "patch",
            "append" => "append",
            "prepend" => "prepend",
            "replacesection" or "section" => "replacesection",
            _ => null
        };
    }

    private FeatureChangeResult DeleteSkill(string? identifier)
    {
        var lookup = ResolveByIdOrLabel(
            _dataStore.Data.Skills,
            identifier,
            static skill => skill.Id,
            static skill => skill.Name,
            Text(nameof(Loc.FeatureManager_EntitySkill)));
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
        _dataStore.DeleteSkillBundle(skill.Id);
        return Success(
            Text("FeatureManager_SkillDeleted", skill.Name, removedFromAgents, removedFromChats),
            syncSkillFiles: true);
    }

    private FeatureChangeResult ListSkills(string? query)
    {
        var skills = FilterByQuery(
                _dataStore.Data.Skills,
                query,
                skill => skill.Name,
                skill => skill.Description)
            .OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var message = skills.Count == 0
            ? string.IsNullOrWhiteSpace(query)
                ? Text(nameof(Loc.FeatureManager_SkillsNone))
                : Text("FeatureManager_SkillsNoMatch", query)
            : Text("FeatureManager_SkillsList", string.Join("\n", skills.Select(DescribeSkill)));
        return new FeatureChangeResult(message, SkillIds: skills.Select(skill => skill.Id).ToArray());
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
            return Failure(Text(nameof(Loc.FeatureManager_AgentNameRequired)));
        if (HasConflictingLabel(_dataStore.Data.Agents, normalizedName, static agent => agent.Name, static agent => agent.Id))
            return Failure(Text("FeatureManager_AgentDuplicateName", normalizedName));

        var skillIdsResult = ResolveSkillIds(skillIdentifiers);
        if (!skillIdsResult.Success)
            return Failure(skillIdsResult.Error!);

        var mcpIdsResult = ResolveMcpIds(mcpServerIdentifiers);
        if (!mcpIdsResult.Success)
            return Failure(mcpIdsResult.Error!);

        var normalizedToolNames = NormalizeList(toolNames);
        var agent = new LumiAgent
        {
            Name = normalizedName,
            Description = NormalizeOrNull(description) ?? "",
            SystemPrompt = NormalizeOrNull(systemPrompt) ?? "",
            IconGlyph = NormalizeOrNull(iconGlyph) ?? "✦",
            SkillIds = skillIdsResult.Items!,
            ToolNames = normalizedToolNames,
            HasExplicitToolSelection = normalizedToolNames.Count > 0,
            McpServerIds = mcpIdsResult.Items!
        };

        _dataStore.Data.Agents.Add(agent);
        return Success(Text("FeatureManager_AgentCreated", DescribeLumi(agent)));
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
            Text(nameof(Loc.FeatureManager_EntityAgent)));
        if (!lookup.Success)
            return Failure(lookup.Error!);

        var agent = lookup.Item!;
        if (name is null && description is null && systemPrompt is null && iconGlyph is null
            && skillIdentifiers is null && toolNames is null && mcpServerIdentifiers is null)
            return Failure(Text(nameof(Loc.FeatureManager_AgentNoChanges)));

        if (name is not null)
        {
            var normalizedName = NormalizeOrNull(name);
            if (normalizedName is null)
                return Failure(Text(nameof(Loc.FeatureManager_AgentNameEmpty)));
            if (HasConflictingLabel(_dataStore.Data.Agents, normalizedName, static item => item.Name, static item => item.Id, agent.Id))
                return Failure(Text("FeatureManager_AgentDuplicateName", normalizedName));
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
        {
            agent.ToolNames = NormalizeList(toolNames);
            agent.HasExplicitToolSelection = agent.ToolNames.Count > 0;
        }

        if (mcpServerIdentifiers is not null)
        {
            var mcpIdsResult = ResolveMcpIds(mcpServerIdentifiers);
            if (!mcpIdsResult.Success)
                return Failure(mcpIdsResult.Error!);
            agent.McpServerIds = mcpIdsResult.Items!;
        }

        return Success(Text("FeatureManager_AgentUpdated", DescribeLumi(agent)));
    }

    private FeatureChangeResult DeleteLumi(string? identifier)
    {
        var lookup = ResolveByIdOrLabel(
            _dataStore.Data.Agents,
            identifier,
            static agent => agent.Id,
            static agent => agent.Name,
            Text(nameof(Loc.FeatureManager_EntityAgent)));
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
        return Success(Text("FeatureManager_AgentDeleted", agent.Name, affectedChats));
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
            return string.IsNullOrWhiteSpace(query)
                ? Text(nameof(Loc.FeatureManager_AgentsNone))
                : Text("FeatureManager_AgentsNoMatch", query);

        return Text("FeatureManager_AgentsList", string.Join("\n", agents.Select(DescribeLumi)));
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
            return Failure(Text(nameof(Loc.FeatureManager_ConnectorNameRequired)));
        if (HasConflictingLabel(_dataStore.Data.McpServers, normalizedName, static server => server.Name, static server => server.Id))
            return Failure(Text("FeatureManager_ConnectorDuplicateName", normalizedName));

        var normalizedType = serverType is null ? "local" : NormalizeServerType(serverType);
        if (normalizedType is null)
            return Failure(Text(nameof(Loc.FeatureManager_ConnectorTypeInvalid)));

        if (!TryParseKeyValueEntries(envEntries, out var env, out var envError))
            return Failure(envError!);
        if (!TryParseKeyValueEntries(headerEntries, out var headers, out var headerError))
            return Failure(headerError!);
        if (timeout is < 0)
            return Failure(Text(nameof(Loc.FeatureManager_ConnectorTimeoutNegative)));

        var normalizedCommand = NormalizeOrNull(command);
        var normalizedUrl = NormalizeOrNull(url);

        if (normalizedType == "local" && normalizedCommand is null)
            return Failure(Text(nameof(Loc.FeatureManager_ConnectorCommandRequired)));
        if (normalizedType == "remote" && normalizedUrl is null)
            return Failure(Text(nameof(Loc.FeatureManager_ConnectorUrlRequired)));

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
        return Success(Text("FeatureManager_ConnectorCreated", DescribeMcp(server)), mcpCatalogChanged: true);
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
            Text(nameof(Loc.FeatureManager_EntityConnector)));
        if (!lookup.Success)
            return Failure(lookup.Error!);

        var server = lookup.Item!;
        if (name is null && description is null && serverType is null && command is null && args is null
            && url is null && envEntries is null && headerEntries is null && toolNames is null
            && timeout is null && clearTimeout != true && isEnabled is null)
            return Failure(Text(nameof(Loc.FeatureManager_ConnectorNoChanges)));

        var previousName = server.Name;
        var previousType = server.ServerType;
        var normalizedType = serverType is null ? server.ServerType : NormalizeServerType(serverType);
        if (serverType is not null && normalizedType is null)
            return Failure(Text(nameof(Loc.FeatureManager_ConnectorTypeInvalid)));

        if (!TryParseKeyValueEntries(envEntries, out var env, out var envError))
            return Failure(envError!);
        if (!TryParseKeyValueEntries(headerEntries, out var headers, out var headerError))
            return Failure(headerError!);
        if (envEntries is not null && !TryKeepSavedValues(env!, server.Env, Text(nameof(Loc.FeatureManager_EnvironmentVariable)), out var keepEnvError))
            return Failure(keepEnvError!);
        if (headerEntries is not null && !TryKeepSavedValues(headers!, server.Headers, Text(nameof(Loc.FeatureManager_Header)), out var keepHeaderError))
            return Failure(keepHeaderError!);
        if (ContainsRedactionMarker(url) || args?.Any(ContainsRedactionMarker) == true)
            return Failure(Text(nameof(Loc.FeatureManager_ConnectorArgumentPlaceholder)));
        if (timeout is < 0)
            return Failure(Text(nameof(Loc.FeatureManager_ConnectorTimeoutNegative)));

        var normalizedCommand = command is null ? null : NormalizeOrNull(command);
        var normalizedUrl = url is null ? null : NormalizeOrNull(url);

        if (normalizedType == "local" && normalizedCommand is null && previousType != "local")
            return Failure(Text(nameof(Loc.FeatureManager_ConnectorSwitchLocalCommandRequired)));
        if (normalizedType == "remote" && normalizedUrl is null && previousType != "remote")
            return Failure(Text(nameof(Loc.FeatureManager_ConnectorSwitchRemoteUrlRequired)));

        if (name is not null)
        {
            var normalizedName = NormalizeOrNull(name);
            if (normalizedName is null)
                return Failure(Text(nameof(Loc.FeatureManager_ConnectorNameEmpty)));
            if (HasConflictingLabel(_dataStore.Data.McpServers, normalizedName, static item => item.Name, static item => item.Id, server.Id))
                return Failure(Text("FeatureManager_ConnectorDuplicateName", normalizedName));
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
            Text("FeatureManager_ConnectorUpdated", DescribeMcp(server)),
            renamedMcpOldName: !string.Equals(previousName, server.Name, StringComparison.Ordinal) ? previousName : null,
            renamedMcpNewName: !string.Equals(previousName, server.Name, StringComparison.Ordinal) ? server.Name : null,
            mcpCatalogChanged: true);
    }

    private FeatureChangeResult DeleteMcp(string? identifier)
    {
        var lookup = ResolveByIdOrLabel(
            _dataStore.Data.McpServers,
            identifier,
            static server => server.Id,
            static server => server.Name,
            Text(nameof(Loc.FeatureManager_EntityConnector)));
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
            Text("FeatureManager_ConnectorDeleted", server.Name, removedFromLumis, removedFromChats),
            deletedMcpName: server.Name,
            mcpCatalogChanged: true);
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
            return string.IsNullOrWhiteSpace(query)
                ? Text(nameof(Loc.FeatureManager_ConnectorsNone))
                : Text("FeatureManager_ConnectorsNoMatch", query);

        return Text("FeatureManager_ConnectorsList", string.Join("\n", servers.Select(DescribeMcp)));
    }

    private FeatureChangeResult CreateMemory(string? key, string? content, string? category)
    {
        var normalizedKey = NormalizeOrNull(key);
        var normalizedContent = NormalizeOrNull(content);
        if (normalizedKey is null)
            return Failure(Text(nameof(Loc.FeatureManager_MemoryKeyRequired)));
        if (normalizedContent is null)
            return Failure(Text(nameof(Loc.FeatureManager_MemoryContentRequired)));
        if (_dataStore.Data.Memories.Any(m => string.Equals(m.Key, normalizedKey, StringComparison.OrdinalIgnoreCase)))
            return Failure(Text("FeatureManager_MemoryDuplicateKeyUseUpdate", normalizedKey));

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
        return Success(Text("FeatureManager_MemoryCreated", DescribeMemory(memory)));
    }

    private FeatureChangeResult UpdateMemory(string? identifier, string? key, string? content, string? category)
    {
        var lookup = ResolveByIdOrLabel(
            _dataStore.Data.Memories,
            identifier,
            static memory => memory.Id,
            static memory => memory.Key,
            Text(nameof(Loc.FeatureManager_EntityMemory)));
        if (!lookup.Success)
            return Failure(lookup.Error!);

        var memory = lookup.Item!;
        if (key is null && content is null && category is null)
            return Failure(Text(nameof(Loc.FeatureManager_MemoryNoChanges)));

        if (key is not null)
        {
            var normalizedKey = NormalizeOrNull(key);
            if (normalizedKey is null)
                return Failure(Text(nameof(Loc.FeatureManager_MemoryKeyEmpty)));
            if (_dataStore.Data.Memories.Any(m =>
                    m.Id != memory.Id && string.Equals(m.Key, normalizedKey, StringComparison.OrdinalIgnoreCase)))
                return Failure(Text("FeatureManager_MemoryDuplicateKey", normalizedKey));
            memory.Key = normalizedKey;
        }

        if (content is not null)
        {
            var normalizedContent = NormalizeOrNull(content);
            if (normalizedContent is null)
                return Failure(Text(nameof(Loc.FeatureManager_MemoryContentEmpty)));
            memory.Content = normalizedContent;
        }

        if (category is not null)
            memory.Category = NormalizeOrNull(category) ?? "General";

        memory.Source = MemorySources.AfterUserEdit(memory.Source);
        memory.UpdatedAt = DateTimeOffset.Now;
        return Success(Text("FeatureManager_MemoryUpdated", DescribeMemory(memory)));
    }

    private FeatureChangeResult DeleteMemory(string? identifier)
    {
        var lookup = ResolveByIdOrLabel(
            _dataStore.Data.Memories,
            identifier,
            static memory => memory.Id,
            static memory => memory.Key,
            Text(nameof(Loc.FeatureManager_EntityMemory)));
        if (!lookup.Success)
            return Failure(lookup.Error!);

        var memory = lookup.Item!;
        _dataStore.Data.Memories.Remove(memory);
        return Success(Text("FeatureManager_MemoryDeleted", memory.Key));
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
            return string.IsNullOrWhiteSpace(query)
                ? Text(nameof(Loc.FeatureManager_MemoriesNone))
                : Text("FeatureManager_MemoriesNoMatch", query);

        return Text("FeatureManager_MemoriesList", string.Join("\n", memories.Select(DescribeMemory)));
    }

    private string DescribeProject(Project project)
    {
        var chatCount = _dataStore.Data.Chats.Count(chat => chat.ProjectId == project.Id);
        var workingDirectory = project.WorkingDirectory ?? Text(nameof(Loc.FeatureManager_None));
        var contextDirectories = project.AdditionalContextDirectories.Count == 0
            ? Text(nameof(Loc.FeatureManager_None))
            : string.Join("; ", project.AdditionalContextDirectories);
        return Text(
            "FeatureManager_ProjectSummary",
            project.Id, project.Name, chatCount, workingDirectory, contextDirectories, Preview(project.Instructions));
    }

    private string DescribeSkill(Skill skill)
    {
        var builtIn = skill.IsBuiltIn ? Text(nameof(Loc.FeatureManager_BuiltIn)) : Text(nameof(Loc.FeatureManager_Custom));
        return $"- {skill.Id} | {skill.IconGlyph} {skill.Name} | {builtIn} | {Preview(_localizeOutput ? skill.DisplayDescription : skill.Description)}";
    }

    private string DescribeLumi(LumiAgent agent)
    {
        var linkedSkills = ResolveNames(agent.SkillIds, _dataStore.Data.Skills, static skill => skill.Id, static skill => skill.Name);
        var linkedMcps = ResolveNames(agent.McpServerIds, _dataStore.Data.McpServers, static server => server.Id, static server => server.Name);
        var tools = !agent.HasToolRestrictions
            ? Text(nameof(Loc.Capability_AllTools))
            : agent.ToolNames.Count == 0
                ? Text(nameof(Loc.Capability_NoTools))
                : string.Join(", ", agent.ToolNames);
        var builtIn = agent.IsBuiltIn ? Text(nameof(Loc.FeatureManager_BuiltIn)) : Text(nameof(Loc.FeatureManager_Custom));
        return Text(
            "FeatureManager_AgentSummary",
            agent.Id, agent.IconGlyph, agent.Name, builtIn, linkedSkills, linkedMcps, tools, Preview(_localizeOutput ? agent.DisplayDescription : agent.Description));
    }

    private string DescribeMcp(McpServer server)
    {
        var endpoint = server.ServerType == "remote"
            ? server.Url
            : $"{server.Command}{(server.Args.Count > 0 ? $" {string.Join(' ', server.Args)}" : "")}";
        var tools = server.Tools.Count == 0 ? Text(nameof(Loc.FeatureManager_AllTools)) : string.Join(", ", server.Tools);
        return Text(
            "FeatureManager_ConnectorSummary",
            server.Id, server.Name, server.ServerType, server.IsEnabled,
            server.Timeout?.ToString() ?? Text(nameof(Loc.FeatureManager_Default)), tools, endpoint!, Preview(server.Description));
    }

    private string DescribeMemory(Memory memory)
    {
        return Text(
            "FeatureManager_MemorySummary",
            memory.Id, memory.Key, (_localizeOutput ? Loc.GetMemoryCategoryDisplay(memory.Category) : memory.Category), memory.UpdatedAt, Preview(memory.Content));
    }

    private string DescribeJob(BackgroundJob job)
    {
        lock (job.SyncRoot)
        {
            var chatTitle = _dataStore.Data.Chats.FirstOrDefault(chat => chat.Id == job.ChatId)?.Title ?? Text(nameof(Loc.FeatureManager_MissingChat));
            var next = job.NextRunAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? Text(nameof(Loc.FeatureManager_None));
            var last = job.LastRunAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? Text(nameof(Loc.FeatureManager_Never));
            var exit = job.LastScriptExitCode.HasValue
                ? Text("FeatureManager_JobExit", job.LastScriptExitCode.Value)
                : "";
            var source = job.TriggerType == BackgroundJobTriggerTypes.ChatEvent
                ? Text("FeatureManager_JobSource",
                    _dataStore.Data.Chats.FirstOrDefault(chat => chat.Id == job.SourceChatId)?.Title ?? Text(nameof(Loc.FeatureManager_MissingChat)))
                : "";
            return Text(
                "FeatureManager_JobSummary",
                job.Id, job.Name, job.IsEnabled ? Text(nameof(Loc.FeatureManager_Enabled)) : Text(nameof(Loc.FeatureManager_Paused)),
                BackgroundJobSchedule.Describe(job, localize: _localizeOutput), chatTitle, source, job.IsTemporary,
                next, last, job.LastRunStatus, exit, Preview(job.Description));
        }
    }

    private string ResolveNames<T>(
        IReadOnlyCollection<Guid> ids,
        IEnumerable<T> items,
        Func<T, Guid> getId,
        Func<T, string> getName)
    {
        if (ids.Count == 0)
            return Text(nameof(Loc.FeatureManager_None));

        var names = items
            .Where(item => ids.Contains(getId(item)))
            .Select(getName)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return names.Count == 0 ? Text(nameof(Loc.FeatureManager_None)) : string.Join(", ", names);
    }

    private static string NormalizeAction(string action)
    {
        return NormalizeOrNull(action)?.ToLowerInvariant() switch
        {
            "list" or "show" or "search" => "list",
            "create" or "add" or "new" => "create",
            "update" or "edit" or "rename" or "modify" => "update",
            "import" or "ingest" or "reload" => "import",
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

    private string Preview(string? text, int maxLength = 80)
    {
        var normalized = NormalizeOrNull(text) ?? Text(nameof(Loc.FeatureManager_Empty));
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..(maxLength - 1)].TrimEnd() + "…";
    }

    private FeatureChangeResult InvalidAction(string entityName, string? action)
    {
        return Failure(Text("FeatureManager_UnsupportedAction", entityName, action ?? "(null)"));
    }

    private static FeatureChangeResult Failure(string message)
        => new(message);

    private static FeatureChangeResult Success(
        string message,
        bool syncSkillFiles = false,
        string? renamedMcpOldName = null,
        string? renamedMcpNewName = null,
        string? deletedMcpName = null,
        int? skillContentBytes = null,
        string? skillContentHash = null,
        bool mcpCatalogChanged = false)
        => new(message, DataChanged: true, SyncSkillFiles: syncSkillFiles,
            RenamedMcpOldName: renamedMcpOldName, RenamedMcpNewName: renamedMcpNewName, DeletedMcpName: deletedMcpName,
            SkillContentBytes: skillContentBytes, SkillContentHash: skillContentHash,
            McpCatalogChanged: mcpCatalogChanged);

    private LookupResult<T> ResolveByIdOrLabel<T>(
        IEnumerable<T> items,
        string? identifier,
        Func<T, Guid> getId,
        Func<T, string> getLabel,
        string entityName)
    {
        var normalizedIdentifier = NormalizeOrNull(identifier);
        if (normalizedIdentifier is null)
            return LookupResult<T>.Fail(Text("FeatureManager_IdentifierRequired", entityName));

        if (Guid.TryParse(normalizedIdentifier, out var id))
        {
            var byId = items.FirstOrDefault(item => getId(item) == id);
            return byId is null
                ? LookupResult<T>.Fail(Text("FeatureManager_NotFound", entityName, normalizedIdentifier))
                : LookupResult<T>.Ok(byId);
        }

        var matches = items
            .Where(item => string.Equals(getLabel(item), normalizedIdentifier, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            0 => LookupResult<T>.Fail(Text("FeatureManager_NotFound", entityName, normalizedIdentifier)),
            1 => LookupResult<T>.Ok(matches[0]),
            _ => LookupResult<T>.Fail(
                Text("FeatureManager_AmbiguousIdentifier", entityName, normalizedIdentifier,
                    string.Join("\n", matches.Select(item => $"- {getId(item)} | {getLabel(item)}"))))
        };
    }

    private LookupManyResult ResolveSkillIds(IEnumerable<string>? identifiers)
    {
        return ResolveIds(
            identifiers,
            _dataStore.Data.Skills,
            static skill => skill.Id,
            static skill => skill.Name,
            Text(nameof(Loc.FeatureManager_EntitySkill)));
    }

    private LookupManyResult ResolveMcpIds(IEnumerable<string>? identifiers)
    {
        return ResolveIds(
            identifiers,
            _dataStore.Data.McpServers,
            static server => server.Id,
            static server => server.Name,
            Text(nameof(Loc.FeatureManager_EntityConnector)));
    }

    private LookupResult<Chat> ResolveChat(string? identifier, Guid? defaultChatId)
    {
        var normalizedIdentifier = NormalizeOrNull(identifier);
        if (normalizedIdentifier is null && defaultChatId.HasValue)
        {
            var defaultChat = _dataStore.Data.Chats.FirstOrDefault(chat => chat.Id == defaultChatId.Value);
            return defaultChat is null
                ? LookupResult<Chat>.Fail(Text("FeatureManager_NotFound", Text(nameof(Loc.FeatureManager_EntityChat)), defaultChatId.Value))
                : LookupResult<Chat>.Ok(defaultChat);
        }

        return ResolveByIdOrLabel(
            _dataStore.Data.Chats,
            normalizedIdentifier,
            static chat => chat.Id,
            static chat => chat.Title,
            Text(nameof(Loc.FeatureManager_EntityChat)));
    }

    private bool TrySetRunAt(BackgroundJob job, string? runAt, out string? error)
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

        error = Text("FeatureManager_RunAtInvalid", runAt);
        return false;
    }

    private LookupManyResult ResolveIds<T>(
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

    /// <summary>
    /// Lumi never shows a connector's saved credentials, only a placeholder. Passing that placeholder back
    /// keeps the saved value, so an update can rewrite the whole list without erasing secrets it can't see.
    /// </summary>
    private bool TryKeepSavedValues(
        Dictionary<string, string> entries,
        IReadOnlyDictionary<string, string> saved,
        string kind,
        out string? error)
    {
        error = null;
        foreach (var key in entries.Keys.ToList())
        {
            if (!IsRedactionPlaceholder(entries[key]))
            {
                if (!ContainsRedactionMarker(entries[key]))
                    continue;

                error = Text("FeatureManager_PlaceholderWouldReplaceValue", kind, key);
                return false;
            }

            var existing = saved.FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(existing.Value))
            {
                error = Text("FeatureManager_PlaceholderHasNoSavedValue", kind, key);
                return false;
            }

            // Kept under its saved name: environment variable names are case-sensitive on Linux and macOS.
            entries.Remove(key);
            entries.TryAdd(existing.Key, existing.Value);
        }

        return true;
    }

    private static bool IsRedactionPlaceholder(string value)
        => value.Trim() is "[REDACTED]" or "<REDACTED>" or "[redacted]" or "<redacted>" or "***" or "******";

    private static bool ContainsRedactionMarker(string? value)
        => value is not null
           && (value.Contains("[REDACTED]", StringComparison.OrdinalIgnoreCase)
               || value.Contains("<REDACTED>", StringComparison.OrdinalIgnoreCase));

    private bool TryParseKeyValueEntries(
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
                error = Text("FeatureManager_KeyValueFormatInvalid", entry);
                values = null;
                return false;
            }

            var key = entry[..separatorIndex].Trim();
            var value = entry[(separatorIndex + 1)..].Trim();
            if (key.Length == 0)
            {
                error = Text("FeatureManager_KeyValueKeyEmpty", entry);
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
