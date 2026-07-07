using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using StrataSearch;

namespace Lumi.ViewModels;

public partial class BackgroundJobsViewModel : ObservableObject
{
    private readonly DataStore _dataStore;
    private readonly BackgroundJobService _jobService;
    private Guid? _preferredChatId;
    private bool _selectPreferredChatOnNextRefresh;
    private bool _isCreatingNewJob;
    private Guid? _preservedEditorJobId;
    private bool _isSynchronizingVisibleSelection;
    private bool _isUpdatingFilters;
    private bool _hydratedIsEnabled;

    public event Action? JobsChanged;
    public event Action<Guid>? OpenChatRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedJob))]
    [NotifyPropertyChangedFor(nameof(SelectedRunStatus))]
    [NotifyPropertyChangedFor(nameof(SelectedNextRunText))]
    [NotifyPropertyChangedFor(nameof(HasSelectedNextRun))]
    [NotifyPropertyChangedFor(nameof(SelectedLastRunSummary))]
    [NotifyPropertyChangedFor(nameof(HasSelectedLastRunSummary))]
    [NotifyPropertyChangedFor(nameof(IsSelectedScriptJob))]
    [NotifyPropertyChangedFor(nameof(SelectedStartedText))]
    [NotifyPropertyChangedFor(nameof(HasSelectedStarted))]
    [NotifyPropertyChangedFor(nameof(SelectedExitCodeText))]
    [NotifyPropertyChangedFor(nameof(HasSelectedExitCode))]
    [NotifyPropertyChangedFor(nameof(SelectedScriptOutput))]
    [NotifyPropertyChangedFor(nameof(HasSelectedScriptOutput))]
    private BackgroundJob? _selectedJob;
    [ObservableProperty] private BackgroundJob? _selectedVisibleJob;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    private string _searchQuery = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    private int _activationFilterIndex;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    private int _lifecycleFilterIndex;
    [ObservableProperty] private int _totalJobCount;
    [ObservableProperty] private string _validationMessage = "";

    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editDescription = "";
    [ObservableProperty] private string _editPrompt = "";
    [ObservableProperty] private Chat? _editChat;
    [ObservableProperty] private int _editTriggerTypeIndex;
    [ObservableProperty] private int _editScheduleTypeIndex;
    [ObservableProperty] private int _editIntervalMinutes = 1440;
    [ObservableProperty] private string _editDailyTime = "08:00";
    [ObservableProperty] private string _editDaysOfWeek = "weekdays";
    [ObservableProperty] private int _editMonthlyDay = 1;
    [ObservableProperty] private string _editCronExpression = "0 8 * * *";
    [ObservableProperty] private string _editRunAt = "";
    [ObservableProperty] private int _editScriptLanguageIndex;
    [ObservableProperty] private string _editScriptContent = "";
    [ObservableProperty] private bool _editIsEnabled = true;
    [ObservableProperty] private bool _editIsTemporary;

    public ObservableCollection<BackgroundJob> Jobs { get; } = [];
    public ObservableCollection<Chat> AvailableChats { get; } = [];

    public bool IsTimeTrigger => EditTriggerTypeIndex == 0;
    public bool IsScriptTrigger => EditTriggerTypeIndex == 1;
    public bool CanUseTemporaryToggle => IsTimeTrigger;
    public bool EditUsesTimeTrigger
    {
        get => EditTriggerTypeIndex == 0;
        set { if (value) EditTriggerTypeIndex = 0; }
    }
    public bool EditUsesScriptTrigger
    {
        get => EditTriggerTypeIndex == 1;
        set { if (value) EditTriggerTypeIndex = 1; }
    }
    public bool IsIntervalSchedule => EditScheduleTypeIndex == 0;
    public bool IsDailySchedule => EditScheduleTypeIndex == 1;
    public bool IsWeeklySchedule => EditScheduleTypeIndex == 2;
    public bool IsMonthlySchedule => EditScheduleTypeIndex == 3;
    public bool IsOnceSchedule => EditScheduleTypeIndex == 4;
    public bool IsCronSchedule => EditScheduleTypeIndex == 5;
    public bool HasSelectedJob => SelectedJob is not null;
    public string SelectedRunStatus => SelectedJob?.LifecycleDisplay ?? "";
    public string SelectedNextRunText => SelectedJob?.NextRunAt?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "";
    public bool HasSelectedNextRun => SelectedJob?.NextRunAt is not null;
    public string SelectedLastRunSummary => SelectedJob switch
    {
        { IsLifecycleInterrupted: true } => Loc.Get("Jobs_LifecycleInterruptedDescription"),
        { } job => job.LastRunSummary,
        _ => ""
    };
    public bool HasSelectedLastRunSummary => !string.IsNullOrWhiteSpace(SelectedJob?.LastRunSummary);
    public bool IsSelectedScriptJob => SelectedJob?.TriggerType == BackgroundJobTriggerTypes.Script;
    public string SelectedStartedText => SelectedJob?.LastRunStartedAt?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "";
    public bool HasSelectedStarted => SelectedJob?.LastRunStartedAt is not null;
    public string SelectedExitCodeText => SelectedJob?.LastScriptExitCode?.ToString(CultureInfo.CurrentCulture) ?? "";
    public bool HasSelectedExitCode => SelectedJob?.LastScriptExitCode is not null;
    public string SelectedScriptOutput => SelectedJob?.LastScriptOutput ?? "";
    public bool HasSelectedScriptOutput => IsSelectedScriptJob && !string.IsNullOrWhiteSpace(SelectedJob?.LastScriptOutput);
    public bool HasActiveFilters => !string.IsNullOrWhiteSpace(SearchQuery)
        || ActivationFilterIndex != 0
        || LifecycleFilterIndex != 0;
    public bool HasVisibleJobs => Jobs.Count > 0;
    public bool HasNoFilterResults => TotalJobCount > 0 && Jobs.Count == 0;
    public string JobCountDisplay => HasActiveFilters
        ? Loc.Get("Jobs_FilterCount", Jobs.Count, TotalJobCount)
        : Loc.Get("Jobs_Count", TotalJobCount);
    public string SelectedActivationStatus => SelectedJob?.ActivationDisplay ?? "";
    public string SelectedActivationDescription => SelectedJob is null
        ? ""
        : Loc.Get(SelectedJob.IsEnabled ? "Jobs_AutomationOn" : "Jobs_AutomationOff");
    public string SelectedActivationActionText => SelectedJob?.IsEnabled == true
        ? Loc.Get("Jobs_PauseAction")
        : Loc.Get("Jobs_EnableAction");
    public string SelectedActivationActionAutomationName => SelectedJob?.ActivationActionAutomationName ?? "";
    public bool IsSelectedJobEnabled => SelectedJob?.IsEnabled == true;
    public bool IsSelectedJobPaused => SelectedJob is { IsEnabled: false };
    public string SelectedLifecycleStatus => SelectedJob?.LifecycleDisplay ?? "";
    public string SelectedLifecycleDescription => SelectedJob switch
    {
        { IsLifecycleInterrupted: true } => Loc.Get("Jobs_LifecycleInterruptedDescription"),
        { LastRunSummary.Length: > 0 } job => job.LastRunSummary,
        { IsLifecycleNotRun: true } => Loc.Get("Jobs_LifecycleNotRunDescription"),
        null => "",
        _ => Loc.Get("Jobs_LifecycleNoSummary")
    };
    public string SelectedUpcomingRunText => SelectedJob?.UpcomingRunDisplay ?? "";
    public string SelectedLastRunTimeText => SelectedJob?.LastRunTimeDisplay ?? "";
    public bool HasSelectedRunHistory => SelectedJob is not null
        && (SelectedJob.RunCount > 0
            || SelectedJob.LastRunStartedAt is not null
            || SelectedJob.LastRunStatus != BackgroundJobRunStatuses.Idle);
    public bool HasNoSelectedRunHistory => !HasSelectedRunHistory;
    public bool IsSelectedLifecycleInProgress => SelectedJob?.IsLifecycleInProgress == true;
    public bool IsSelectedLifecycleCompleted => SelectedJob?.IsLifecycleCompleted == true;
    public bool IsSelectedLifecycleSkipped => SelectedJob?.IsLifecycleSkipped == true;
    public bool IsSelectedLifecycleFailed => SelectedJob?.IsLifecycleFailed == true;
    public bool IsSelectedLifecycleIdle => SelectedJob?.IsLifecycleIdle == true;

    public BackgroundJobsViewModel(DataStore dataStore, BackgroundJobService jobService)
    {
        _dataStore = dataStore;
        _jobService = jobService;
        RefreshFromStore();
    }

    public void RefreshFromStore(bool preserveEditorBuffer = false)
    {
        var preservedEditChatId = preserveEditorBuffer ? EditChat?.Id : null;
        RefreshChats();
        if (preserveEditorBuffer)
        {
            EditChat = preservedEditChatId is { } chatId
                ? AvailableChats.FirstOrDefault(chat => chat.Id == chatId)
                : null;
        }

        RefreshList();

        if (TrySelectPreferredChatJob())
            return;

        if (SelectedJob is null)
        {
            SelectDefaultJobIfNeeded();
            return;
        }

        var selected = _dataStore.SnapshotBackgroundJobs().FirstOrDefault(job => job.Id == SelectedJob.Id);
        if (selected is null)
        {
            SelectedJob = null;
            IsEditing = false;
            SelectDefaultJobIfNeeded();
            return;
        }

        if (!ReferenceEquals(SelectedJob, selected))
        {
            SelectedJob = selected;
            return;
        }

        if (preserveEditorBuffer)
            RefreshEditorRuntimeState(selected);
        else
            SyncEditorFromJob(selected);

        RefreshSelectedJobDerivedProperties();
    }

    private void RefreshChats()
    {
        AvailableChats.Clear();
        foreach (var chat in _dataStore.Data.Chats.OrderByDescending(chat => chat.UpdatedAt))
            AvailableChats.Add(chat);
    }

    public void SetPreferredChat(Chat? chat)
    {
        _preferredChatId = chat?.Id;
        _selectPreferredChatOnNextRefresh = _preferredChatId is not null;
    }

    private Chat? GetPreferredChat()
    {
        if (_preferredChatId is not { } chatId)
            return null;

        return AvailableChats.FirstOrDefault(chat => chat.Id == chatId);
    }

    private void RefreshList()
    {
        var selectedJobId = SelectedJob?.Id;
        _preservedEditorJobId = selectedJobId;
        try
        {
            var jobs = _dataStore.SnapshotBackgroundJobs();
            TotalJobCount = jobs.Count;
            var orderedJobs = jobs
                .OrderByDescending(static job => job.IsEnabled)
                .ThenBy(static job => job.NextRunAt ?? DateTimeOffset.MaxValue)
                .ThenBy(static job => job.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var job in orderedJobs)
                job.NotifyPresentationStateChanged();

            var filteredJobs = orderedJobs
                .Where(MatchesActivationFilter)
                .Where(MatchesLifecycleFilter)
                .ToArray();
            var trimmedQuery = SearchQuery.Trim();
            var hasQuery = trimmedQuery.Length > 0;
            var searchCandidates = hasQuery
                ? filteredJobs.Where(job => MatchesSearchQuery(job, trimmedQuery)).ToArray()
                : filteredJobs;
            var items = hasQuery
                ? SearchPipeline.Rank(
                    searchCandidates,
                    trimmedQuery,
                    static job =>
                    [
                        SearchField.Primary(job.Name, 3.5),
                        new SearchField(job.Description, 1.8)
                    ])
                : searchCandidates;

            ReconcileJobs(items.ToArray());

            if (!_isCreatingNewJob)
            {
                var storedSelection = selectedJobId is { } id
                    ? jobs.FirstOrDefault(job => job.Id == id)
                    : null;

                if (storedSelection is not null)
                {
                    if (!ReferenceEquals(SelectedJob, storedSelection))
                        SelectedJob = storedSelection;

                    SynchronizeVisibleSelection(storedSelection);
                }
                else
                {
                    SelectedJob = null;
                    IsEditing = false;
                    SelectDefaultJobIfNeeded();
                }
            }

            RefreshListDerivedProperties();
        }
        finally
        {
            _preservedEditorJobId = null;
        }
    }

    private void ReconcileJobs(BackgroundJob[] items)
    {
        for (var targetIndex = 0; targetIndex < items.Length; targetIndex++)
        {
            var target = items[targetIndex];
            var currentIndex = -1;
            for (var index = targetIndex; index < Jobs.Count; index++)
            {
                if (Jobs[index].Id != target.Id)
                    continue;

                currentIndex = index;
                break;
            }

            if (currentIndex < 0)
            {
                Jobs.Insert(targetIndex, target);
                continue;
            }

            if (!ReferenceEquals(Jobs[currentIndex], target))
                Jobs[currentIndex] = target;

            if (currentIndex != targetIndex)
                Jobs.Move(currentIndex, targetIndex);
        }

        while (Jobs.Count > items.Length)
            Jobs.RemoveAt(Jobs.Count - 1);
    }

    private bool MatchesActivationFilter(BackgroundJob job) => ActivationFilterIndex switch
    {
        1 => job.IsEnabled,
        2 => !job.IsEnabled,
        _ => true
    };

    private static bool MatchesSearchQuery(BackgroundJob job, string query)
    {
        var terms = query.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return terms.All(term =>
            job.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)
            || job.Description.Contains(term, StringComparison.CurrentCultureIgnoreCase));
    }

    private bool MatchesLifecycleFilter(BackgroundJob job) => LifecycleFilterIndex switch
    {
        1 => job.IsLifecycleInProgress,
        2 => job.IsLifecycleFinished,
        3 => job.IsLifecycleFailed,
        4 => job.IsLifecycleNotRun,
        _ => true
    };

    private void RefreshListDerivedProperties()
    {
        OnPropertyChanged(nameof(HasVisibleJobs));
        OnPropertyChanged(nameof(HasNoFilterResults));
        OnPropertyChanged(nameof(JobCountDisplay));
    }

    private void SelectDefaultJobIfNeeded()
    {
        if (SelectedJob is not null || _isCreatingNewJob || Jobs.Count == 0)
            return;

        SelectedJob = GetDefaultJobSelection();
    }

    private bool TrySelectPreferredChatJob()
    {
        if (!_selectPreferredChatOnNextRefresh)
            return false;

        _selectPreferredChatOnNextRefresh = false;
        if (_preferredChatId is not { } preferredChatId)
            return false;

        if (_isCreatingNewJob)
            return false;

        var preferredJob = Jobs.FirstOrDefault(job => job.ChatId == preferredChatId);
        if (preferredJob is null || SelectedJob?.Id == preferredJob.Id)
            return false;

        SelectedJob = preferredJob;
        return true;
    }

    private BackgroundJob? GetDefaultJobSelection()
    {
        if (_preferredChatId is { } preferredChatId)
        {
            var preferredJob = Jobs.FirstOrDefault(job => job.ChatId == preferredChatId);
            if (preferredJob is not null)
                return preferredJob;
        }

        return Jobs.FirstOrDefault();
    }

    partial void OnSearchQueryChanged(string value)
    {
        if (!_isUpdatingFilters)
            RefreshList();
    }

    partial void OnActivationFilterIndexChanged(int value)
    {
        if (!_isUpdatingFilters)
            RefreshList();
    }

    partial void OnLifecycleFilterIndexChanged(int value)
    {
        if (!_isUpdatingFilters)
            RefreshList();
    }

    partial void OnEditTriggerTypeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsTimeTrigger));
        OnPropertyChanged(nameof(IsScriptTrigger));
        OnPropertyChanged(nameof(CanUseTemporaryToggle));
        OnPropertyChanged(nameof(EditUsesTimeTrigger));
        OnPropertyChanged(nameof(EditUsesScriptTrigger));
        if (value == 1)
            EditIsTemporary = true;
    }

    partial void OnEditScheduleTypeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsIntervalSchedule));
        OnPropertyChanged(nameof(IsDailySchedule));
        OnPropertyChanged(nameof(IsWeeklySchedule));
        OnPropertyChanged(nameof(IsMonthlySchedule));
        OnPropertyChanged(nameof(IsOnceSchedule));
        OnPropertyChanged(nameof(IsCronSchedule));
    }

    partial void OnSelectedJobChanged(BackgroundJob? value)
    {
        var preserveEditorBuffer = value is not null && _preservedEditorJobId == value.Id;
        if (!preserveEditorBuffer)
            ValidationMessage = "";

        SynchronizeVisibleSelection(value);
        RefreshSelectedJobDerivedProperties();
        if (value is null)
            return;

        _isCreatingNewJob = false;
        if (preserveEditorBuffer)
        {
            IsEditing = true;
            RefreshSelectedJobDerivedProperties();
            return;
        }

        SyncEditorFromJob(value);
        IsEditing = true;
        RefreshSelectedJobDerivedProperties();
    }

    partial void OnSelectedVisibleJobChanged(BackgroundJob? value)
    {
        if (_isSynchronizingVisibleSelection || value is null || SelectedJob?.Id == value.Id)
            return;

        SelectedJob = value;
    }

    private void SynchronizeVisibleSelection(BackgroundJob? job)
    {
        var visibleJob = job is null
            ? null
            : Jobs.FirstOrDefault(item => item.Id == job.Id);
        if (ReferenceEquals(SelectedVisibleJob, visibleJob))
            return;

        _isSynchronizingVisibleSelection = true;
        try
        {
            SelectedVisibleJob = visibleJob;
        }
        finally
        {
            _isSynchronizingVisibleSelection = false;
        }
    }

    private void SyncEditorFromJob(BackgroundJob job)
    {
        EditName = job.Name;
        EditDescription = job.Description;
        EditPrompt = job.Prompt;
        EditChat = AvailableChats.FirstOrDefault(chat => chat.Id == job.ChatId);
        EditTriggerTypeIndex = job.TriggerType == BackgroundJobTriggerTypes.Script ? 1 : 0;
        EditScheduleTypeIndex = job.ScheduleType switch
        {
            BackgroundJobScheduleTypes.Daily => 1,
            BackgroundJobScheduleTypes.Weekly => 2,
            BackgroundJobScheduleTypes.Monthly => 3,
            BackgroundJobScheduleTypes.Once => 4,
            BackgroundJobScheduleTypes.Cron => 5,
            _ => 0
        };
        EditIntervalMinutes = Math.Max(1, job.IntervalMinutes);
        EditDailyTime = string.IsNullOrWhiteSpace(job.DailyTime) ? "08:00" : job.DailyTime;
        EditDaysOfWeek = string.IsNullOrWhiteSpace(job.DaysOfWeek) ? "weekdays" : job.DaysOfWeek;
        EditMonthlyDay = Math.Clamp(job.MonthlyDay, 1, 31);
        EditCronExpression = string.IsNullOrWhiteSpace(job.CronExpression) ? "0 8 * * *" : job.CronExpression;
        EditRunAt = job.RunAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) ?? "";
        EditScriptLanguageIndex = ScriptLanguageToIndex(job.ScriptLanguage);
        EditScriptContent = job.ScriptContent;
        EditIsEnabled = job.IsEnabled;
        _hydratedIsEnabled = job.IsEnabled;
        EditIsTemporary = job.IsTemporary;
    }

    private void RefreshEditorRuntimeState(BackgroundJob job)
    {
        if (EditIsEnabled == _hydratedIsEnabled)
            EditIsEnabled = job.IsEnabled;

        _hydratedIsEnabled = job.IsEnabled;
    }

    [RelayCommand]
    private void ClearSearch() => SearchQuery = "";

    [RelayCommand]
    private void ResetFilters()
    {
        _isUpdatingFilters = true;
        try
        {
            SearchQuery = "";
            ActivationFilterIndex = 0;
            LifecycleFilterIndex = 0;
        }
        finally
        {
            _isUpdatingFilters = false;
        }

        RefreshList();
    }

    [RelayCommand]
    private void NewJob()
    {
        SelectedJob = null;
        _isCreatingNewJob = true;
        ValidationMessage = "";
        EditName = "";
        EditDescription = "";
        EditPrompt = "";
        EditChat = GetPreferredChat() ?? AvailableChats.FirstOrDefault();
        EditTriggerTypeIndex = 0;
        EditScheduleTypeIndex = 0;
        EditIntervalMinutes = 1440;
        EditDailyTime = "08:00";
        EditDaysOfWeek = "weekdays";
        EditMonthlyDay = DateTimeOffset.Now.Day;
        EditCronExpression = "0 8 * * *";
        EditRunAt = DateTimeOffset.Now.AddHours(1).ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
        EditScriptLanguageIndex = ScriptLanguageToIndex(BackgroundJobScriptLanguages.DefaultForCurrentOs());
        EditScriptContent = "";
        EditIsEnabled = true;
        _hydratedIsEnabled = true;
        EditIsTemporary = false;
        IsEditing = true;
    }

    private bool ValidateTriggerConfiguration()
    {
        if (!IsTimeTrigger)
            return true;

        if ((IsDailySchedule || IsWeeklySchedule || IsMonthlySchedule)
            && !BackgroundJobSchedule.TryValidateDailyTime(EditDailyTime, out var timeError))
        {
            ValidationMessage = timeError;
            return false;
        }

        if (IsWeeklySchedule
            && !BackgroundJobSchedule.TryValidateDaysOfWeek(EditDaysOfWeek, out var daysError))
        {
            ValidationMessage = daysError;
            return false;
        }

        if (IsMonthlySchedule && (EditMonthlyDay < 1 || EditMonthlyDay > 31))
        {
            ValidationMessage = "Monthly day must be between 1 and 31.";
            return false;
        }

        if (IsCronSchedule
            && !BackgroundJobSchedule.TryValidateCronExpression(EditCronExpression, out var cronError))
        {
            ValidationMessage = cronError;
            return false;
        }

        return true;
    }

    [RelayCommand]
    private void SaveJob()
    {
        ValidationMessage = "";
        if (string.IsNullOrWhiteSpace(EditName))
        {
            ValidationMessage = "Name is required.";
            return;
        }

        if (string.IsNullOrWhiteSpace(EditPrompt))
        {
            ValidationMessage = "Job instructions are required.";
            return;
        }

        if (EditChat is null)
        {
            ValidationMessage = "Choose the chat this job belongs to.";
            return;
        }

        if (!ValidateTriggerConfiguration())
            return;

        if (IsScriptTrigger && string.IsNullOrWhiteSpace(EditScriptContent))
        {
            ValidationMessage = "Wake scripts need a script.";
            return;
        }

        var now = DateTimeOffset.Now;
        var isNewJob = SelectedJob is null;
        var job = SelectedJob ?? new BackgroundJob { CreatedAt = DateTimeOffset.Now };
        DateTimeOffset? parsedRunAt = null;
        if (IsTimeTrigger && IsOnceSchedule && !TryParseRunAt(EditRunAt, out parsedRunAt))
        {
            ValidationMessage = "Run once date/time could not be parsed.";
            return;
        }

        var shouldRunImmediately = false;
        lock (job.SyncRoot)
        {
            job.Name = EditName.Trim();
            job.Description = EditDescription.Trim();
            job.Prompt = EditPrompt.Trim();
            job.ChatId = EditChat.Id;
            job.TriggerType = IsScriptTrigger ? BackgroundJobTriggerTypes.Script : BackgroundJobTriggerTypes.Time;
            job.ScheduleType = EditScheduleTypeIndex switch
            {
                1 => BackgroundJobScheduleTypes.Daily,
                2 => BackgroundJobScheduleTypes.Weekly,
                3 => BackgroundJobScheduleTypes.Monthly,
                4 => BackgroundJobScheduleTypes.Once,
                5 => BackgroundJobScheduleTypes.Cron,
                _ => BackgroundJobScheduleTypes.Interval
            };
            job.IntervalMinutes = Math.Max(1, EditIntervalMinutes);
            job.DailyTime = string.IsNullOrWhiteSpace(EditDailyTime) ? "08:00" : EditDailyTime.Trim();
            job.DaysOfWeek = string.IsNullOrWhiteSpace(EditDaysOfWeek) ? "weekdays" : EditDaysOfWeek.Trim();
            job.MonthlyDay = Math.Clamp(EditMonthlyDay, 1, 31);
            job.CronExpression = string.IsNullOrWhiteSpace(EditCronExpression) ? "0 8 * * *" : EditCronExpression.Trim();
            job.ScriptContent = EditScriptContent.Trim();
            job.ScriptLanguage = IndexToScriptLanguage(EditScriptLanguageIndex);
            job.IsEnabled = EditIsEnabled;
            job.IsTemporary = IsScriptTrigger || EditIsTemporary;
            job.UpdatedAt = now;
            job.RunAt = parsedRunAt;

            BackgroundJobSchedule.Normalize(job);
            job.NextRunAt = job.IsEnabled ? BackgroundJobSchedule.ComputeNextRun(job, now, afterRun: false) : null;
            shouldRunImmediately = job.IsEnabled && job.NextRunAt is not null && job.NextRunAt <= now;
        }

        if (isNewJob)
            _dataStore.AddBackgroundJob(job);
        else
            _dataStore.MarkBackgroundJobsChanged();

        _isCreatingNewJob = false;
        SelectedJob = job;
        _hydratedIsEnabled = job.IsEnabled;
        _ = _dataStore.SaveAsync();
        IsEditing = true;
        RefreshList();
        RefreshSelectedJobDerivedProperties();
        JobsChanged?.Invoke();
        if (shouldRunImmediately)
            _ = _jobService.RunDueJobsNowAsync();
    }

    [RelayCommand]
    private void CancelEdit()
    {
        ValidationMessage = "";
        _isCreatingNewJob = false;
        IsEditing = false;
    }

    [RelayCommand]
    private void DeleteJob(BackgroundJob? job)
    {
        job ??= SelectedJob;
        if (job is null)
            return;

        _dataStore.RemoveBackgroundJob(job);
        if (SelectedJob?.Id == job.Id)
            SelectedJob = null;
        _isCreatingNewJob = false;
        _ = _dataStore.SaveAsync();
        IsEditing = false;
        RefreshList();
        SelectDefaultJobIfNeeded();
        JobsChanged?.Invoke();
    }

    [RelayCommand]
    private void ToggleJob(BackgroundJob? job)
    {
        if (job is null)
            return;
        if (!job.IsEnabled && !EnsureLinkedChatExists(job))
            return;

        ValidationMessage = "";
        var now = DateTimeOffset.Now;
        var shouldRunImmediately = false;
        lock (job.SyncRoot)
        {
            job.IsEnabled = !job.IsEnabled;
            job.NextRunAt = job.IsEnabled ? BackgroundJobSchedule.ComputeNextRun(job, now, afterRun: false) : null;
            job.UpdatedAt = now;
            shouldRunImmediately = job.IsEnabled && job.NextRunAt is not null && job.NextRunAt <= now;
        }

        _dataStore.MarkBackgroundJobsChanged();
        _ = _dataStore.SaveAsync();
        if (SelectedJob?.Id == job.Id)
        {
            EditIsEnabled = job.IsEnabled;
            _hydratedIsEnabled = job.IsEnabled;
        }
        RefreshList();
        RefreshSelectedJobDerivedProperties();
        JobsChanged?.Invoke();
        if (shouldRunImmediately)
            _ = _jobService.RunDueJobsNowAsync();
    }

    [RelayCommand]
    private void RunNow(BackgroundJob? job)
    {
        job ??= SelectedJob;
        if (job is null)
            return;
        if (!EnsureLinkedChatExists(job))
            return;

        ValidationMessage = "";
        lock (job.SyncRoot)
        {
            job.IsEnabled = true;
            job.NextRunAt = DateTimeOffset.Now;
            job.UpdatedAt = DateTimeOffset.Now;
        }

        _dataStore.MarkBackgroundJobsChanged();
        _ = _dataStore.SaveAsync();
        if (SelectedJob?.Id == job.Id)
        {
            EditIsEnabled = true;
            _hydratedIsEnabled = true;
        }
        RefreshList();
        RefreshSelectedJobDerivedProperties();
        JobsChanged?.Invoke();
        _ = _jobService.RunDueJobsNowAsync();
    }

    private void RefreshSelectedJobDerivedProperties()
    {
        OnPropertyChanged(nameof(HasSelectedJob));
        OnPropertyChanged(nameof(SelectedRunStatus));
        OnPropertyChanged(nameof(SelectedNextRunText));
        OnPropertyChanged(nameof(HasSelectedNextRun));
        OnPropertyChanged(nameof(SelectedLastRunSummary));
        OnPropertyChanged(nameof(HasSelectedLastRunSummary));
        OnPropertyChanged(nameof(IsSelectedScriptJob));
        OnPropertyChanged(nameof(SelectedStartedText));
        OnPropertyChanged(nameof(HasSelectedStarted));
        OnPropertyChanged(nameof(SelectedExitCodeText));
        OnPropertyChanged(nameof(HasSelectedExitCode));
        OnPropertyChanged(nameof(SelectedScriptOutput));
        OnPropertyChanged(nameof(HasSelectedScriptOutput));
        OnPropertyChanged(nameof(SelectedActivationStatus));
        OnPropertyChanged(nameof(SelectedActivationDescription));
        OnPropertyChanged(nameof(SelectedActivationActionText));
        OnPropertyChanged(nameof(SelectedActivationActionAutomationName));
        OnPropertyChanged(nameof(IsSelectedJobEnabled));
        OnPropertyChanged(nameof(IsSelectedJobPaused));
        OnPropertyChanged(nameof(SelectedLifecycleStatus));
        OnPropertyChanged(nameof(SelectedLifecycleDescription));
        OnPropertyChanged(nameof(SelectedUpcomingRunText));
        OnPropertyChanged(nameof(SelectedLastRunTimeText));
        OnPropertyChanged(nameof(HasSelectedRunHistory));
        OnPropertyChanged(nameof(HasNoSelectedRunHistory));
        OnPropertyChanged(nameof(IsSelectedLifecycleInProgress));
        OnPropertyChanged(nameof(IsSelectedLifecycleCompleted));
        OnPropertyChanged(nameof(IsSelectedLifecycleSkipped));
        OnPropertyChanged(nameof(IsSelectedLifecycleFailed));
        OnPropertyChanged(nameof(IsSelectedLifecycleIdle));
    }

    [RelayCommand]
    private void OpenChat(BackgroundJob? job)
    {
        if (job is null || !EnsureLinkedChatExists(job))
            return;

        ValidationMessage = "";
        OpenChatRequested?.Invoke(job.ChatId);
    }

    private bool EnsureLinkedChatExists(BackgroundJob job)
    {
        if (_dataStore.Data.Chats.Any(chat => chat.Id == job.ChatId))
            return true;

        ValidationMessage = Loc.Get("Jobs_LinkedChatMissing");
        return false;
    }

    private static int ScriptLanguageToIndex(string? language)
    {
        return BackgroundJobSchedule.NormalizeScriptLanguage(language) switch
        {
            BackgroundJobScriptLanguages.Python => 1,
            BackgroundJobScriptLanguages.Node => 2,
            BackgroundJobScriptLanguages.Command => 3,
            _ => 0
        };
    }

    private static string IndexToScriptLanguage(int index)
    {
        return index switch
        {
            1 => BackgroundJobScriptLanguages.Python,
            2 => BackgroundJobScriptLanguages.Node,
            3 => BackgroundJobScriptLanguages.Command,
            _ => BackgroundJobScriptLanguages.PowerShell
        };
    }

    private static bool TryParseRunAt(string value, out DateTimeOffset? runAt)
    {
        runAt = null;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        if (DateTimeOffset.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var dto))
        {
            runAt = dto;
            return true;
        }

        if (DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var local))
        {
            runAt = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
            return true;
        }

        return false;
    }
}
