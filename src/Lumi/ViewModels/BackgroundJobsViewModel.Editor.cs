using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;

namespace Lumi.ViewModels;

/// <summary>The job editor: what changed since it was stored, saving, discarding and deleting.</summary>
public partial class BackgroundJobsViewModel
{
    private bool _syncingEditor;
    private bool _isRefreshing;
    private bool _syncingChips;
    private EditorSnapshot _editorBaseline;
    private int _savedToastVersion;

    /// <summary>The open job differs from what is stored, or a new job has been filled in.</summary>
    [ObservableProperty] private bool _hasUnsavedChanges;
    [ObservableProperty] private bool _isConfirmingDelete;
    [ObservableProperty] private bool _showSavedToast;

    /// <summary>The days a weekly job runs on, as chips in the order the user's calendar uses.</summary>
    public ObservableCollection<JobDayOption> WeekDays { get; }

    /// <summary>The source chat's events a chat-event job wakes on, as chips.</summary>
    public ObservableCollection<JobEventOption> ChatEventOptions { get; }

    public bool ShowOverview => !IsEditing;
    public bool IsNewJob => IsEditing && _isCreatingNewJob;
    public bool HasUnsavedWork => IsEditing && (HasUnsavedChanges || _isCreatingNewJob);
    public bool ShowSaveBar => HasUnsavedWork && !IsConfirmingDelete;
    public string SaveButtonText => IsNewJob ? Loc.Jobs_Create : Loc.Mg_SaveChanges;
    public string SaveBarText => IsNewJob ? Loc.Mg_NotSavedYet : Loc.Mg_Unsaved;
    public string DiscardButtonText => IsNewJob ? Loc.Common_Cancel : Loc.Mg_Discard;
    public string DetailTitle => string.IsNullOrWhiteSpace(EditName) ? Loc.Jobs_New : EditName.Trim();
    public string DeleteConfirmText => string.Format(
        CultureInfo.CurrentCulture, Loc.Mg_DeleteConfirm, SelectedJob?.Name ?? DetailTitle);

    public bool IsPowerShellScript => EditScriptLanguageIndex == 0;
    public bool IsPythonScript => EditScriptLanguageIndex == 1;
    public bool IsNodeScript => EditScriptLanguageIndex == 2;
    public bool IsCommandScript => EditScriptLanguageIndex == 3;

    /// <summary>The schedule being edited, in words, while it is valid: "Every day at 09:15".</summary>
    public string EditScheduleSummary { get; private set; } = "";

    /// <summary>When the schedule being edited would run next: "Next run tomorrow 09:15".</summary>
    public string EditNextRunText { get; private set; } = "";

    public bool HasEditSchedulePreview => EditScheduleSummary.Length > 0;
    public bool HasEditNextRun => EditNextRunText.Length > 0;

    private readonly record struct EditorSnapshot(
        string Name,
        string Description,
        string Prompt,
        Guid? ChatId,
        Guid? SourceChatId,
        ScheduleSnapshot Schedule,
        int ScriptLanguage,
        string ScriptContent,
        string ChatEventTypes,
        bool IsTemporary);

    /// <summary>What decides when a time job runs next.</summary>
    private readonly record struct ScheduleSnapshot(
        int TriggerType,
        int ScheduleType,
        int IntervalMinutes,
        string DailyTime,
        string DaysOfWeek,
        int MonthlyDay,
        string CronExpression,
        string RunAt,
        bool IsEnabled);

    private EditorSnapshot CaptureEditor() => new(
        EditName,
        EditDescription,
        EditPrompt,
        EditChat?.Id,
        EditSourceChat?.Id,
        CaptureSchedule(),
        EditScriptLanguageIndex,
        EditScriptContent,
        CanonicalChatEvents(EditChatEventTypes),
        EditIsTemporary);

    private ScheduleSnapshot CaptureSchedule() => new(
        EditTriggerTypeIndex,
        EditScheduleTypeIndex,
        EditIntervalMinutes,
        EditDailyTime,
        CanonicalDays(EditDaysOfWeek),
        EditMonthlyDay,
        EditCronExpression,
        EditRunAt,
        EditIsEnabled);

    private void ResetEditorBaseline()
    {
        _editorBaseline = CaptureEditor();
        UpdateDirtyState();
    }

    /// <summary>
    /// A run, or a toggle outside the editor, changed whether the open job is enabled: the editor's
    /// switch followed it, so that is not an edit of the user's.
    /// </summary>
    private void FollowStoredActivation(bool isEnabled)
    {
        _editorBaseline = _editorBaseline with { Schedule = _editorBaseline.Schedule with { IsEnabled = isEnabled } };
        UpdateDirtyState();
        RefreshTriggerPreview();
    }

    private void UpdateDirtyState()
    {
        if (_syncingEditor || _isRefreshing)
            return;

        HasUnsavedChanges = IsEditing && CaptureEditor() != _editorBaseline;
        NotifyEditorState();
    }

    private void NotifyEditorState()
    {
        OnPropertyChanged(nameof(IsNewJob));
        OnPropertyChanged(nameof(HasUnsavedWork));
        OnPropertyChanged(nameof(ShowSaveBar));
        OnPropertyChanged(nameof(SaveButtonText));
        OnPropertyChanged(nameof(SaveBarText));
        OnPropertyChanged(nameof(DiscardButtonText));
        OnPropertyChanged(nameof(DetailTitle));
        OnPropertyChanged(nameof(DeleteConfirmText));
    }

    partial void OnIsEditingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowOverview));
        if (!value)
        {
            // However the editor closes, history's Back included, none of it stays behind.
            ValidationMessage = "";
            IsConfirmingDelete = false;
            _isCreatingNewJob = false;
        }

        UpdateDirtyState();
    }

    partial void OnHasUnsavedChangesChanged(bool value) => NotifyEditorState();

    partial void OnIsConfirmingDeleteChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSaveBar));
        OnPropertyChanged(nameof(DeleteConfirmText));
    }

    partial void OnEditNameChanged(string value) => UpdateDirtyState();
    partial void OnEditDescriptionChanged(string value) => UpdateDirtyState();
    partial void OnEditPromptChanged(string value) => UpdateDirtyState();
    partial void OnEditChatChanged(Chat? value) => UpdateDirtyState();
    partial void OnEditSourceChatChanged(Chat? value) => UpdateDirtyState();
    partial void OnEditScriptContentChanged(string value) => UpdateDirtyState();
    partial void OnEditIsEnabledChanged(bool value) => OnScheduleFieldChanged();
    partial void OnEditIsTemporaryChanged(bool value) => UpdateDirtyState();

    partial void OnEditIntervalMinutesChanged(int value) => OnScheduleFieldChanged();
    partial void OnEditDailyTimeChanged(string value) => OnScheduleFieldChanged();
    partial void OnEditMonthlyDayChanged(int value) => OnScheduleFieldChanged();
    partial void OnEditCronExpressionChanged(string value) => OnScheduleFieldChanged();
    partial void OnEditRunAtChanged(string value) => OnScheduleFieldChanged();

    partial void OnEditDaysOfWeekChanged(string value)
    {
        if (!_syncingChips)
            SyncDayChips();
        OnScheduleFieldChanged();
    }

    partial void OnEditChatEventTypesChanged(string value)
    {
        if (!_syncingChips)
            SyncChatEventChips();
        UpdateDirtyState();
    }

    partial void OnEditScriptLanguageIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsPowerShellScript));
        OnPropertyChanged(nameof(IsPythonScript));
        OnPropertyChanged(nameof(IsNodeScript));
        OnPropertyChanged(nameof(IsCommandScript));
        UpdateDirtyState();
    }

    private void OnScheduleFieldChanged()
    {
        RefreshTriggerPreview();
        UpdateDirtyState();
    }

    /// <summary>Throws away edits: an open job goes back to what is stored, a new one is dropped.</summary>
    [RelayCommand]
    private void DiscardChanges()
    {
        ValidationMessage = "";
        if (SelectedJob is { } job)
        {
            SyncEditorFromJob(job);
            return;
        }

        CloseDetail();
    }

    [RelayCommand]
    private void RequestDelete()
    {
        if (SelectedJob is not null)
            IsConfirmingDelete = true;
    }

    [RelayCommand]
    private void CancelDelete() => IsConfirmingDelete = false;

    [RelayCommand]
    private void ConfirmDelete()
    {
        IsConfirmingDelete = false;
        if (SelectedJob is { } job)
            DeleteJob(job);
    }

    [RelayCommand]
    private void DismissValidation() => ValidationMessage = "";

    /// <summary>Opens a job from the overview.</summary>
    private void OpenJob(BackgroundJob job)
    {
        if (ReferenceEquals(SelectedJob, job))
        {
            IsEditing = true;
            return;
        }

        SelectedJob = job;
    }

    /// <summary>Picks how a time job repeats: 0 interval, 1 daily, 2 weekly, 3 monthly, 4 once, 5 cron.</summary>
    [RelayCommand]
    private void SetScheduleType(string? index)
    {
        if (int.TryParse(index, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value is >= 0 and <= 5)
            EditScheduleTypeIndex = value;
    }

    /// <summary>Picks what runs a wake script: 0 PowerShell, 1 Python, 2 Node.js, 3 the command shell.</summary>
    [RelayCommand]
    private void SetScriptLanguage(string? index)
    {
        if (int.TryParse(index, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value is >= 0 and <= 3)
            EditScriptLanguageIndex = value;
    }

    private async void FlashSaved()
    {
        var version = ++_savedToastVersion;
        ShowSavedToast = true;
        await Task.Delay(1800);
        if (version == _savedToastVersion)
            ShowSavedToast = false;
    }

    private ObservableCollection<JobDayOption> CreateWeekDays()
    {
        var format = Loc.Culture.DateTimeFormat;
        var days = new ObservableCollection<JobDayOption>();
        for (var offset = 0; offset < 7; offset++)
        {
            var day = (DayOfWeek)(((int)format.FirstDayOfWeek + offset) % 7);
            days.Add(new JobDayOption(day, format.GetAbbreviatedDayName(day), ToggleDay));
        }

        return days;
    }

    private ObservableCollection<JobEventOption> CreateChatEventOptions() =>
    [
        new(ChatLifecycleEventTypes.Idle, Loc.Jobs_EventIdle, ToggleChatEvent),
        new(ChatLifecycleEventTypes.TurnEnd, Loc.Jobs_EventTurnEnd, ToggleChatEvent),
        new(ChatLifecycleEventTypes.TurnStart, Loc.Jobs_EventTurnStart, ToggleChatEvent),
        new(ChatLifecycleEventTypes.Error, Loc.Jobs_EventError, ToggleChatEvent),
        new(ChatLifecycleEventTypes.Aborted, Loc.Jobs_EventAborted, ToggleChatEvent),
        new(ChatLifecycleEventTypes.Any, Loc.Jobs_EventAny, ToggleChatEvent)
    ];

    /// <summary>A weekly job keeps at least one day, so the last chosen day can't be turned off.</summary>
    private void ToggleDay(JobDayOption option)
    {
        if (option.IsSelected && WeekDays.Count(static day => day.IsSelected) == 1)
            return;

        option.IsSelected = !option.IsSelected;
        var days = WeekDays
            .Where(static day => day.IsSelected)
            .Select(static day => day.Day)
            .OrderBy(static day => ((int)day + 6) % 7)
            .Select(static day => day.ToString()[..3]);
        _syncingChips = true;
        try
        {
            EditDaysOfWeek = string.Join(",", days);
        }
        finally
        {
            _syncingChips = false;
        }
    }

    private void SyncDayChips()
    {
        var days = BackgroundJobSchedule.TryParseDaysOfWeek(EditDaysOfWeek ?? "", out var parsed)
            ? parsed
            : [];
        foreach (var option in WeekDays)
            option.IsSelected = days.Contains(option.Day);
    }

    /// <summary>A chat-event job keeps at least one event, so the last chosen one can't be turned off.</summary>
    private void ToggleChatEvent(JobEventOption option)
    {
        if (option.IsSelected && ChatEventOptions.Count(static item => item.IsSelected) == 1)
            return;

        option.IsSelected = !option.IsSelected;
        var tokens = ChatLifecycleEventTypes.Supported
            .Where(token => ChatEventOptions.Any(item => item.IsSelected && item.Token == token));
        _syncingChips = true;
        try
        {
            EditChatEventTypes = string.Join(", ", tokens);
        }
        finally
        {
            _syncingChips = false;
        }
    }

    private void SyncChatEventChips()
    {
        var tokens = ChatLifecycleEventTypes.TryNormalize([EditChatEventTypes ?? ""], out var normalized, out _, defaultToIdle: true)
            ? normalized
            : [];
        foreach (var option in ChatEventOptions)
            option.IsSelected = tokens.Contains(option.Token, StringComparer.Ordinal);
    }

    private static string CanonicalDays(string? value)
    {
        if (!BackgroundJobSchedule.TryParseDaysOfWeek(value ?? "", out var days))
            return value ?? "";

        return string.Join(",", days.Distinct().OrderBy(static day => day));
    }

    private static string CanonicalChatEvents(string? value)
    {
        if (!ChatLifecycleEventTypes.TryNormalize([value ?? ""], out var tokens, out _, defaultToIdle: true))
            return value ?? "";

        return string.Join(",", tokens.OrderBy(static token => token, StringComparer.Ordinal));
    }

    /// <summary>
    /// Describes the schedule being edited and when it would run next, so the user sees what they set
    /// up in words before saving. Nothing is shown while a field is invalid. Until its schedule or
    /// switch is edited, a job runs when it is stored to, as its status says.
    /// </summary>
    private void RefreshTriggerPreview()
    {
        var summary = "";
        var next = "";
        if (IsTimeTrigger && TryBuildEditedSchedule(out var job))
        {
            var now = DateTimeOffset.Now;
            summary = BackgroundJobSchedule.Describe(job);
            var nextRun = SelectedJob is { } stored && CaptureSchedule() == _editorBaseline.Schedule
                ? (stored.IsEnabled ? stored.NextRunAt : null)
                : BackgroundJobSchedule.ComputeNextRun(job, now, afterRun: false);
            if (nextRun is { } at)
                next = Loc.Get("Jobs_NextRunAt", LaunchpadPlanner.FormatRunTime(at, now));
        }

        if (summary != EditScheduleSummary)
        {
            EditScheduleSummary = summary;
            OnPropertyChanged(nameof(EditScheduleSummary));
            OnPropertyChanged(nameof(HasEditSchedulePreview));
        }

        if (next != EditNextRunText)
        {
            EditNextRunText = next;
            OnPropertyChanged(nameof(EditNextRunText));
            OnPropertyChanged(nameof(HasEditNextRun));
        }
    }

    private bool TryBuildEditedSchedule(out BackgroundJob job)
    {
        // An edited schedule runs as saving would set it: from the job's last run, or from now.
        var now = DateTimeOffset.Now;
        job = new BackgroundJob
        {
            TriggerType = BackgroundJobTriggerTypes.Time,
            ScheduleType = IndexToScheduleType(EditScheduleTypeIndex),
            IntervalMinutes = Math.Max(1, EditIntervalMinutes),
            DailyTime = (EditDailyTime ?? "").Trim(),
            DaysOfWeek = (EditDaysOfWeek ?? "").Trim(),
            MonthlyDay = EditMonthlyDay,
            CronExpression = (EditCronExpression ?? "").Trim(),
            IsEnabled = EditIsEnabled,
            CreatedAt = SelectedJob?.CreatedAt ?? now,
            UpdatedAt = now,
            LastRunAt = SelectedJob?.LastRunAt
        };

        if ((IsDailySchedule || IsWeeklySchedule || IsMonthlySchedule)
            && !BackgroundJobSchedule.TryValidateDailyTime(job.DailyTime, out _))
            return false;
        if (IsWeeklySchedule && !BackgroundJobSchedule.TryValidateDaysOfWeek(job.DaysOfWeek, out _))
            return false;
        if (IsMonthlySchedule && EditMonthlyDay is < 1 or > 31)
            return false;
        if (IsCronSchedule && !BackgroundJobSchedule.TryValidateCronExpression(job.CronExpression, out _))
            return false;
        if (IsOnceSchedule)
        {
            if (!TryParseRunAt(EditRunAt ?? "", out var runAt))
                return false;
            job.RunAt = runAt;
        }

        return true;
    }
}
