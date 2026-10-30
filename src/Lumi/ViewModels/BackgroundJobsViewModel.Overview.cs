using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Lumi.Localization;
using Lumi.Models;

namespace Lumi.ViewModels;

/// <summary>
/// The Jobs overview: jobs grouped by how they are doing (running now, needing attention, enabled,
/// paused), the sidebar's state facets, and the facts the open job's header shows.
/// </summary>
public partial class BackgroundJobsViewModel
{
    private readonly Dictionary<Guid, JobCard> _cards = [];

    public ObservableCollection<JobCard> RunningCards { get; } = [];
    public ObservableCollection<JobCard> AttentionCards { get; } = [];
    public ObservableCollection<JobCard> EnabledCards { get; } = [];
    public ObservableCollection<JobCard> PausedCards { get; } = [];

    public JobFacet AllFacet { get; }
    public JobFacet EnabledFacet { get; }
    public JobFacet PausedFacet { get; }
    public JobFacet InProgressFacet { get; }
    public JobFacet FailedFacet { get; }

    public bool HasRunningCards => RunningCards.Count > 0;
    public bool HasAttentionCards => AttentionCards.Count > 0;
    public bool HasEnabledCards => EnabledCards.Count > 0;
    public bool HasPausedCards => PausedCards.Count > 0;
    public string RunningCardsCountText => Count(RunningCards.Count);
    public string AttentionCardsCountText => Count(AttentionCards.Count);
    public string EnabledCardsCountText => Count(EnabledCards.Count);
    public string PausedCardsCountText => Count(PausedCards.Count);

    public bool ShowEmptyState => TotalJobCount == 0;
    public bool ShowStats => TotalJobCount > 0;
    public int EnabledJobCount { get; private set; }
    public int PausedJobCount { get; private set; }
    public int RunningJobCount { get; private set; }
    public int AttentionJobCount { get; private set; }
    public string EnabledJobCountText => Count(EnabledJobCount);
    public string PausedJobCountText => Count(PausedJobCount);
    public string RunningJobCountText => Count(RunningJobCount);
    public string AttentionJobCountText => Count(AttentionJobCount);
    public bool HasRunningJobs => RunningJobCount > 0;
    public bool HasAttentionJobs => AttentionJobCount > 0;

    /// <summary>When the soonest enabled job runs: "19:46", "tomorrow 09:15".</summary>
    public string NextRunValue { get; private set; } = "";

    /// <summary>Which job that is: "Next: London hotel price watch".</summary>
    public string NextRunLabel { get; private set; } = "";
    public bool HasNextRun => NextRunValue.Length > 0;

    public string SelectedChatTitle => SelectedJob is { } job ? ChatTitle(job.ChatId) : "";
    public bool HasSelectedChat => SelectedChatTitle.Length > 0;
    public string SelectedScheduleText => SelectedJob?.ScheduleDisplay ?? "";
    public bool IsSelectedRunning => SelectedJob?.IsRunning == true;
    public bool IsSelectedFailing => SelectedJob is { IsRunning: false, IsLifecycleFailed: true };

    /// <summary>When the open job runs next ("Tomorrow 09:15"), or why it doesn't.</summary>
    public string SelectedNextRunValue => SelectedJob switch
    {
        { IsRunning: false, IsEnabled: true, NextRunAt: { } at } => Standalone(LaunchpadPlanner.FormatRunTime(at, DateTimeOffset.Now)),
        { } job => job.UpcomingRunDisplay,
        _ => ""
    };

    /// <summary>The open job's next run is a time, rather than a reason there is none.</summary>
    public bool HasSelectedNextRunTime => SelectedJob is { IsRunning: false, IsEnabled: true, NextRunAt: not null };

    public string SelectedLastRunRelative => SelectedJob?.LastRunAt is { } at ? ManagementText.Relative(at) : "";
    public string SelectedLastRunDate => SelectedJob?.LastRunAt is { } at
        ? at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
        : "";
    public bool HasSelectedLastRunTime => SelectedJob?.LastRunAt is not null;

    private static string Count(int count) => count.ToString(CultureInfo.CurrentCulture);

    /// <summary>A run time phrased for the middle of a sentence ("tomorrow 09:15"), standing on its own.</summary>
    private static string Standalone(string text)
        => text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];

    private string ChatTitle(Guid chatId)
        => _dataStore.Data.Chats.FirstOrDefault(chat => chat.Id == chatId)?.Title ?? "";

    private void RefreshOverview(
        IReadOnlyList<BackgroundJob> jobs,
        IReadOnlyList<BackgroundJob> searchedJobs,
        IReadOnlyList<BackgroundJob> visibleJobs)
    {
        var now = DateTimeOffset.Now;
        var chatTitles = new Dictionary<Guid, string>();
        foreach (var chat in _dataStore.Data.Chats)
            chatTitles.TryAdd(chat.Id, chat.Title);

        List<JobCard> running = [], attention = [], enabled = [], paused = [];
        var shown = new HashSet<Guid>();
        foreach (var job in visibleJobs)
        {
            shown.Add(job.Id);
            if (!_cards.TryGetValue(job.Id, out var card))
            {
                card = new JobCard(OpenJob, ToggleJob, RunNow);
                _cards[job.Id] = card;
            }

            card.Update(job, chatTitles.GetValueOrDefault(job.ChatId) ?? "", now);
            var group = job.IsRunning ? running
                : job.IsEnabled && job.IsLifecycleFailed ? attention
                : job.IsEnabled ? enabled
                : paused;
            group.Add(card);
        }

        foreach (var id in _cards.Keys.Where(id => !shown.Contains(id)).ToList())
            _cards.Remove(id);

        ReconcileCards(RunningCards, running);
        ReconcileCards(AttentionCards, attention);
        ReconcileCards(EnabledCards, enabled);
        ReconcileCards(PausedCards, paused);

        AllFacet.Count = searchedJobs.Count;
        EnabledFacet.Count = searchedJobs.Count(static job => job.IsEnabled);
        PausedFacet.Count = searchedJobs.Count(static job => !job.IsEnabled);
        InProgressFacet.Count = searchedJobs.Count(static job => job.IsLifecycleInProgress);
        FailedFacet.Count = searchedJobs.Count(static job => job.IsLifecycleFailed);
        foreach (var facet in new[] { AllFacet, EnabledFacet, PausedFacet, InProgressFacet, FailedFacet })
            facet.IsSelected = facet.ActivationFilter == ActivationFilterIndex && facet.LifecycleFilter == LifecycleFilterIndex;

        EnabledJobCount = jobs.Count(static job => job.IsEnabled);
        PausedJobCount = jobs.Count - EnabledJobCount;
        RunningJobCount = jobs.Count(static job => job.IsRunning);
        AttentionJobCount = jobs.Count(static job => !job.IsRunning && job.IsEnabled && job.IsLifecycleFailed);
        var next = jobs
            .Where(static job => job is { IsEnabled: true, IsRunning: false, NextRunAt: not null })
            .OrderBy(static job => job.NextRunAt)
            .FirstOrDefault();
        NextRunValue = next?.NextRunAt is { } at ? Standalone(LaunchpadPlanner.FormatRunTime(at, now)) : "";
        NextRunLabel = next is null ? "" : Loc.Get("Jobs_StatNext", next.Name);

        OnPropertyChanged(nameof(HasRunningCards));
        OnPropertyChanged(nameof(HasAttentionCards));
        OnPropertyChanged(nameof(HasEnabledCards));
        OnPropertyChanged(nameof(HasPausedCards));
        OnPropertyChanged(nameof(RunningCardsCountText));
        OnPropertyChanged(nameof(AttentionCardsCountText));
        OnPropertyChanged(nameof(EnabledCardsCountText));
        OnPropertyChanged(nameof(PausedCardsCountText));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(ShowStats));
        OnPropertyChanged(nameof(EnabledJobCountText));
        OnPropertyChanged(nameof(PausedJobCountText));
        OnPropertyChanged(nameof(RunningJobCountText));
        OnPropertyChanged(nameof(AttentionJobCountText));
        OnPropertyChanged(nameof(HasRunningJobs));
        OnPropertyChanged(nameof(HasAttentionJobs));
        OnPropertyChanged(nameof(NextRunValue));
        OnPropertyChanged(nameof(NextRunLabel));
        OnPropertyChanged(nameof(HasNextRun));
        RefreshSelectedJobPresentation();
    }

    private static void ReconcileCards(ObservableCollection<JobCard> target, List<JobCard> items)
    {
        for (var index = target.Count - 1; index >= 0; index--)
        {
            if (!items.Contains(target[index]))
                target.RemoveAt(index);
        }

        for (var index = 0; index < items.Count; index++)
        {
            var current = target.IndexOf(items[index]);
            if (current < 0)
                target.Insert(index, items[index]);
            else if (current != index)
                target.Move(current, index);
        }
    }

    private JobFacet CreateFacet(int activationFilter, int lifecycleFilter)
        => new(activationFilter, lifecycleFilter, SelectFacet);

    /// <summary>A sidebar facet narrows the list and the overview to one state; "All jobs" clears it.</summary>
    private void SelectFacet(JobFacet facet)
    {
        _isUpdatingFilters = true;
        try
        {
            ActivationFilterIndex = facet.ActivationFilter;
            LifecycleFilterIndex = facet.LifecycleFilter;
        }
        finally
        {
            _isUpdatingFilters = false;
        }

        RefreshList();
    }

    private void RefreshSelectedJobPresentation()
    {
        OnPropertyChanged(nameof(SelectedChatTitle));
        OnPropertyChanged(nameof(HasSelectedChat));
        OnPropertyChanged(nameof(SelectedScheduleText));
        OnPropertyChanged(nameof(IsSelectedRunning));
        OnPropertyChanged(nameof(IsSelectedFailing));
        OnPropertyChanged(nameof(SelectedNextRunValue));
        OnPropertyChanged(nameof(HasSelectedNextRunTime));
        OnPropertyChanged(nameof(SelectedLastRunRelative));
        OnPropertyChanged(nameof(SelectedLastRunDate));
        OnPropertyChanged(nameof(HasSelectedLastRunTime));
    }
}
