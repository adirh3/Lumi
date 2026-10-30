using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;

namespace Lumi.ViewModels;

/// <summary>
/// A job on the Jobs overview: what it does, when it runs and how its last run went. The page
/// refreshes its cards on the UI thread whenever jobs change, so a card never listens to a job a
/// run is updating on another thread.
/// </summary>
public sealed class JobCard : ObservableObject
{
    private BackgroundJob _model = new();
    private DateTimeOffset _now;

    public JobCard(Action<BackgroundJob> open, Action<BackgroundJob> toggle, Action<BackgroundJob> runNow)
    {
        OpenCommand = new RelayCommand(() => open(_model));
        ToggleCommand = new RelayCommand(() => toggle(_model));
        RunNowCommand = new RelayCommand(() => runNow(_model));
    }

    public BackgroundJob Model => _model;
    public string Name => _model.Name;
    public string ChatTitle { get; private set; } = "";
    public bool HasChat => ChatTitle.Length > 0;
    public string Summary => string.IsNullOrWhiteSpace(_model.Description)
        ? ManagementText.PlainPreview(_model.Prompt, 180)
        : _model.Description;
    public bool HasSummary => Summary.Length > 0;
    public string ScheduleLabel => _model.ScheduleDisplay;
    public bool IsTimeTriggered => _model.IsTimeTriggered;
    public bool IsScriptTriggered => _model.IsScriptTriggered;
    public bool IsChatEventTriggered => _model.IsChatEventTriggered;
    public bool IsEnabled => _model.IsEnabled;
    public bool IsRunning => _model.IsRunning;
    public bool IsFailed => !_model.IsRunning && _model.IsLifecycleFailed;
    public bool IsPaused => !_model.IsEnabled && !_model.IsRunning;

    /// <summary>The card's footer: what it is doing, when it runs next, or when it last ran.</summary>
    public string StatusText
    {
        get
        {
            if (_model.IsRunning)
                return _model.LifecycleDisplay;
            if (IsFailed)
                return _model.LastRunAt is { } failedAt
                    ? _model.LifecycleDisplay + " · " + ManagementText.Relative(failedAt)
                    : _model.LifecycleDisplay;
            if (_model.IsEnabled && _model.NextRunAt is { } nextRun)
                return Loc.Get("Jobs_NextRunAt", LaunchpadPlanner.FormatRunTime(nextRun, _now));
            if (_model.LastRunAt is { } lastRun)
                return Loc.Get("Jobs_LastRunAt", ManagementText.Relative(lastRun));
            return _model.IsEnabled ? _model.UpcomingRunDisplay : _model.LifecycleDisplay;
        }
    }

    public string ToggleAutomationName => _model.ActivationActionAutomationName;
    public string RunNowAutomationName => _model.RunNowAutomationName;

    public IRelayCommand OpenCommand { get; }
    public IRelayCommand ToggleCommand { get; }
    public IRelayCommand RunNowCommand { get; }

    internal void Update(BackgroundJob job, string chatTitle, DateTimeOffset now)
    {
        _model = job;
        ChatTitle = chatTitle;
        _now = now;
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>A state the Jobs sidebar narrows to, with how many jobs are in it.</summary>
public sealed partial class JobFacet : ObservableObject
{
    public JobFacet(int activationFilter, int lifecycleFilter, Action<JobFacet> select)
    {
        ActivationFilter = activationFilter;
        LifecycleFilter = lifecycleFilter;
        SelectCommand = new RelayCommand(() => select(this));
    }

    public int ActivationFilter { get; }
    public int LifecycleFilter { get; }
    public IRelayCommand SelectCommand { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    [NotifyPropertyChangedFor(nameof(HasItems))]
    private int _count;

    [ObservableProperty] private bool _isSelected;

    public string CountText => Count.ToString(CultureInfo.CurrentCulture);
    public bool HasItems => Count > 0;
}

/// <summary>A day a weekly job can run on.</summary>
public sealed partial class JobDayOption : ObservableObject
{
    public JobDayOption(DayOfWeek day, string label, Action<JobDayOption> toggle)
    {
        Day = day;
        Label = label;
        ToggleCommand = new RelayCommand(() => toggle(this));
    }

    public DayOfWeek Day { get; }
    public string Label { get; }
    public IRelayCommand ToggleCommand { get; }

    [ObservableProperty] private bool _isSelected;
}

/// <summary>An event of the source chat that wakes a chat-event job.</summary>
public sealed partial class JobEventOption : ObservableObject
{
    public JobEventOption(string token, string label, Action<JobEventOption> toggle)
    {
        Token = token;
        Label = label;
        ToggleCommand = new RelayCommand(() => toggle(this));
    }

    public string Token { get; }
    public string Label { get; }
    public IRelayCommand ToggleCommand { get; }

    [ObservableProperty] private bool _isSelected;
}
