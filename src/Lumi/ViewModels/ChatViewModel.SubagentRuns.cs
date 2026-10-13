using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using StrataTheme.Controls;

namespace Lumi.ViewModels;

/// <summary>Which runs the Workspace's Agents index shows.</summary>
public enum SubagentRunFilter
{
    All,
    Running,
    Done,
    Failed,
    Stopped,
}

/// <summary>A heading inside the Agents index ("Running now" / "Finished") with its run count.</summary>
public sealed partial class SubagentIndexSection : ObservableObject
{
    public SubagentIndexSection(string title, bool isLive)
    {
        Title = title;
        IsLive = isLive;
    }

    public string Title { get; }

    /// <summary>The section holds agents that are working right now.</summary>
    public bool IsLive { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountLabel))]
    private int _count;

    public string CountLabel => Count.ToString(CultureInfo.CurrentCulture);
}

/// <summary>One status filter on the Agents index, with how many runs it holds.</summary>
public sealed partial class SubagentFilterChip : ObservableObject
{
    private readonly Action<SubagentRunFilter> _select;

    public SubagentFilterChip(SubagentRunFilter filter, string title, Action<SubagentRunFilter> select)
    {
        Filter = filter;
        Title = title;
        _select = select;
    }

    public SubagentRunFilter Filter { get; }

    public string Title { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountLabel), nameof(IsAvailable))]
    private int _count;

    [ObservableProperty] private bool _isSelected;

    public string CountLabel => Count.ToString(CultureInfo.CurrentCulture);

    /// <summary>Only statuses that actually occur are offered.</summary>
    public bool IsAvailable => Filter == SubagentRunFilter.All || Count > 0;

    [RelayCommand]
    private void Select() => _select(Filter);
}

/// <summary>
/// Sub-agent run inspection: the chat-wide index of every agent Lumi delegated to, and the
/// read-only run page shown on the Workspace's Agents page.
/// </summary>
public partial class ChatViewModel
{
    /// <summary>Raised when a sub-agent run (or the run index) should open in the Workspace.</summary>
    public event Action? SubagentRunShowRequested;

    /// <summary>Raised to close the Workspace's Agents page.</summary>
    public event Action? SubagentRunHideRequested;

    /// <summary>Every sub-agent run in the open chat, oldest first.</summary>
    public ObservableCollection<SubagentToolCallItem> SubagentRuns => _transcriptBuilder.SubagentRuns;

    [ObservableProperty] private SubagentToolCallItem? _selectedSubagentRun;
    [ObservableProperty] private int _subagentRunCount;
    [ObservableProperty] private int _runningSubagentCount;

    /// <summary>
    /// The open run rendered with the chat's own transcript pipeline, so its steps group and read
    /// exactly like Lumi's. Replaced on a rebuild, hence the notification; live work extends it.
    /// </summary>
    [ObservableProperty] private ObservableCollection<TranscriptTurn> _subagentRunTurns = [];

    /// <summary>
    /// What the Agents index lists, in display order: <see cref="SubagentIndexSection"/> headings and
    /// <see cref="SubagentToolCallItem"/> runs. Running agents lead, then the newest finished ones.
    /// Replaced wholesale when its runs are new instances (a chat switch); patched in place otherwise.
    /// </summary>
    [ObservableProperty] private ObservableCollection<object> _subagentIndexRows = [];

    /// <summary>The index's status filters ("All 12", "Running 2", "Failed 1"…).</summary>
    public ObservableCollection<SubagentFilterChip> SubagentFilterChips => _subagentFilterChips ??= CreateSubagentFilterChips();

    [ObservableProperty] private SubagentRunFilter _subagentRunFilter = SubagentRunFilter.All;

    /// <summary>Filters only earn their row once the chat holds runs in more than one state.</summary>
    [ObservableProperty] private bool _showSubagentFilters;

    private ObservableCollection<SubagentFilterChip>? _subagentFilterChips;
    private SubagentIndexSection? _runningSubagentSection;
    private SubagentIndexSection? _finishedSubagentSection;

    /// <summary>Every run in index order (running first, then newest first), before filtering.</summary>
    private IReadOnlyList<SubagentToolCallItem> _orderedSubagentRuns = [];

    /// <summary>The runs the index currently shows, in its order; the run page pages through these.</summary>
    private IReadOnlyList<SubagentToolCallItem> _visibleSubagentRuns = [];

    private SubagentRunTranscript? _subagentRunTranscript;
    private UiThrottler? _subagentRunRebuild;
    private ObservableCollection<TranscriptTurn>? _observedSubagentRunTurns;

    /// <summary>True while the Agents page shows the index of all runs rather than a single run.</summary>
    public bool IsSubagentIndexVisible => SelectedSubagentRun is null;

    public bool HasSubagentRuns => SubagentRunCount > 0;
    public bool HasRunningSubagents => RunningSubagentCount > 0;

    /// <summary>True once the open run has produced anything worth reading.</summary>
    public bool HasSubagentRunContent => SubagentRunTurns.Count > 0;

    /// <summary>The open run has nothing to show and isn't working on anything either.</summary>
    public bool ShowSubagentRunEmptyState => !HasSubagentRunContent && SelectedSubagentRun is { IsInProgress: false };

    /// <summary>Header badge text: how many agents are running, or how many finished.</summary>
    public string SubagentRunsSummary => RunningSubagentCount > 0
        ? string.Format(Loc.Subagent_RunningOfTotal, RunningSubagentCount, SubagentRunCount)
        : string.Format(Loc.Subagent_AllFinished, SubagentRunCount);

    /// <summary>The run page can step to a neighbouring run.</summary>
    public bool HasSubagentRunPager => SelectedSubagentRun is not null && PagedSubagentRuns.Count > 1;

    /// <summary>"3 of 12": where the open run sits in the index.</summary>
    public string SubagentRunPositionText
    {
        get
        {
            var runs = PagedSubagentRuns;
            var index = IndexOfSelectedRun(runs);
            return index < 0
                ? ""
                : string.Format(Loc.Culture, Loc.Subagent_RunPosition, index + 1, runs.Count);
        }
    }

    /// <summary>
    /// Set while the open run isn't in the index's filtered view — it was opened from the chat, or
    /// it has since left the filter — so the pager steps through every run. Decided when a run is
    /// opened rather than on each step, so paging never switches which list it walks.
    /// </summary>
    private bool _pageAcrossAllRuns;

    /// <summary>The runs the pager steps through.</summary>
    private IReadOnlyList<SubagentToolCallItem> PagedSubagentRuns
        => _pageAcrossAllRuns ? _orderedSubagentRuns : _visibleSubagentRuns;

    private int IndexOfSelectedRun(IReadOnlyList<SubagentToolCallItem> runs)
    {
        if (SelectedSubagentRun is not { } selected)
            return -1;

        for (var i = 0; i < runs.Count; i++)
        {
            if (ReferenceEquals(runs[i], selected))
                return i;
        }

        return -1;
    }

    private bool CanOpenPreviousSubagentRun() => IndexOfSelectedRun(PagedSubagentRuns) > 0;

    private bool CanOpenNextSubagentRun()
    {
        var runs = PagedSubagentRuns;
        var index = IndexOfSelectedRun(runs);
        return index >= 0 && index < runs.Count - 1;
    }

    /// <summary>Steps the run page to the run above the open one in the index.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenPreviousSubagentRun))]
    private void OpenPreviousSubagentRun() => StepSubagentRun(-1);

    /// <summary>Steps the run page to the run below the open one in the index.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenNextSubagentRun))]
    private void OpenNextSubagentRun() => StepSubagentRun(1);

    private void StepSubagentRun(int delta)
    {
        var runs = PagedSubagentRuns;
        var index = IndexOfSelectedRun(runs) + delta;
        if (index >= 0 && index < runs.Count)
            SelectedSubagentRun = runs[index];
    }

    partial void OnSubagentRunTurnsChanged(ObservableCollection<TranscriptTurn> value)
    {
        // Live work extends the same collection, so the empty state follows its count, not just its swaps.
        if (_observedSubagentRunTurns is not null)
            _observedSubagentRunTurns.CollectionChanged -= OnSubagentRunTurnsCollectionChanged;
        _observedSubagentRunTurns = value;
        value.CollectionChanged += OnSubagentRunTurnsCollectionChanged;
        NotifySubagentRunContentChanged();
    }

    private void OnSubagentRunTurnsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => NotifySubagentRunContentChanged();

    private void NotifySubagentRunContentChanged()
    {
        OnPropertyChanged(nameof(HasSubagentRunContent));
        OnPropertyChanged(nameof(ShowSubagentRunEmptyState));
    }

    partial void OnSelectedSubagentRunChanged(SubagentToolCallItem? oldValue, SubagentToolCallItem? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.RunContentChanged -= OnSubagentRunContentChanged;
            oldValue.Activities.CollectionChanged -= OnSubagentRunActivitiesChanged;
        }

        if (newValue is not null)
        {
            newValue.RunContentChanged += OnSubagentRunContentChanged;
            newValue.Activities.CollectionChanged += OnSubagentRunActivitiesChanged;
        }
        else
        {
            _pageAcrossAllRuns = false;
        }

        OnPropertyChanged(nameof(IsSubagentIndexVisible));
        OnPropertyChanged(nameof(ShowSubagentRunEmptyState));
        NotifySubagentPagerChanged();
        _subagentRunRebuild?.CancelPending();
        RebuildSubagentRunTranscript();
    }

    partial void OnSubagentRunFilterChanged(SubagentRunFilter value) => RefreshSubagentIndex();

    private void OnSubagentRunContentChanged()
    {
        // Content changes include the run reaching a terminal status.
        OnPropertyChanged(nameof(ShowSubagentRunEmptyState));
        RequestSubagentRunRebuild();
    }

    private void OnSubagentRunActivitiesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => RequestSubagentRunRebuild();

    /// <summary>
    /// Coalesces run syncs. A streaming agent changes its text many times a second; each sync is
    /// incremental, but batching keeps a busy agent to a steady refresh cadence instead of per token.
    /// </summary>
    private void RequestSubagentRunRebuild()
    {
        if (SelectedSubagentRun is null)
            return;

        _subagentRunRebuild ??= new UiThrottler(RebuildSubagentRunTranscript, TimeSpan.FromMilliseconds(120));
        _subagentRunRebuild.Request();
    }

    private void RebuildSubagentRunTranscript()
    {
        if (SelectedSubagentRun is not { } run)
        {
            _subagentRunTranscript?.Clear();
            SubagentRunTurns = [];
            return;
        }

        _subagentRunTranscript ??= new SubagentRunTranscript(
            _dataStore,
            item => DiffShowRequested?.Invoke(item));
        _subagentRunTranscript.Sync(run, Messages);
        if (!ReferenceEquals(SubagentRunTurns, _subagentRunTranscript.Turns))
            SubagentRunTurns = _subagentRunTranscript.Turns;
    }

    partial void OnSubagentRunCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasSubagentRuns));
        OnPropertyChanged(nameof(SubagentRunsSummary));
        OnPropertyChanged(nameof(WorkspaceToggleToolTip));
    }

    partial void OnRunningSubagentCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasRunningSubagents));
        OnPropertyChanged(nameof(SubagentRunsSummary));
        OnPropertyChanged(nameof(WorkspaceToggleToolTip));
    }

    /// <summary>
    /// Recomputes the chat-wide agent counters after the transcript builder adds a run or one of
    /// them changes state. A transcript rebuild replaces every run instance, so an open run is
    /// re-pointed at its rebuilt twin (stable ids survive rebuilds). When no twin exists the run is
    /// genuinely gone — a chat switch, or a resend that truncated it — so the selection is dropped
    /// rather than left pointing at a detached item from the previous transcript.
    /// </summary>
    private void RefreshSubagentRunState()
    {
        var runs = _transcriptBuilder.SubagentRuns;
        int running = 0, failed = 0, stopped = 0;
        foreach (var run in runs)
        {
            switch (run.Status)
            {
                case StrataAiToolCallStatus.InProgress: running++; break;
                case StrataAiToolCallStatus.Failed: failed++; break;
                case StrataAiToolCallStatus.Stopped: stopped++; break;
            }
        }

        SubagentRunCount = runs.Count;
        RunningSubagentCount = running;

        // Mid-rebuild the collection is only partially populated, so a selection can't be judged
        // missing yet. The builder re-notifies once the rebuild completes.
        if (_transcriptBuilder.IsRebuildingTranscript)
            return;

        // Streaming agents report here many times a second. Order and membership only change when a
        // run is added, replaced (a rebuild) or changes state, so the lists are left alone otherwise;
        // each row follows its own run's text and progress. An active Workspace search is the
        // exception: it matches run titles, which change as agents work.
        var signature = new SubagentIndexSignature(
            runs.Count, running, failed, stopped,
            runs.Count > 0 ? runs[0] : null,
            runs.Count > 0 ? runs[^1] : null);
        if (signature == _subagentIndexSignature
            && !HasWorkspaceSearch
            && (SelectedSubagentRun is null || runs.Contains(SelectedSubagentRun)))
        {
            return;
        }

        _subagentIndexSignature = signature;
        _orderedSubagentRuns = OrderSubagentRuns(runs);
        RefreshWorkspaceAgents(_orderedSubagentRuns);

        if (SelectedSubagentRun is { } selected && !runs.Contains(selected))
            SelectedSubagentRun = ResolveReplacementRun(selected);

        RefreshSubagentIndex();
    }

    private readonly record struct SubagentIndexSignature(
        int Total,
        int Running,
        int Failed,
        int Stopped,
        SubagentToolCallItem? First,
        SubagentToolCallItem? Last);

    private SubagentIndexSignature _subagentIndexSignature;

    /// <summary>Running agents lead so live work is visible without scrolling; then newest first.</summary>
    private static List<SubagentToolCallItem> OrderSubagentRuns(IList<SubagentToolCallItem> runs)
    {
        var ordered = new List<SubagentToolCallItem>(runs.Count);
        for (var i = runs.Count - 1; i >= 0; i--)
        {
            if (runs[i].IsInProgress)
                ordered.Add(runs[i]);
        }

        for (var i = runs.Count - 1; i >= 0; i--)
        {
            if (!runs[i].IsInProgress)
                ordered.Add(runs[i]);
        }

        return ordered;
    }

    private static bool MatchesSubagentFilter(SubagentToolCallItem run, SubagentRunFilter filter) => filter switch
    {
        SubagentRunFilter.Running => run.Status == StrataAiToolCallStatus.InProgress,
        SubagentRunFilter.Done => run.Status == StrataAiToolCallStatus.Completed,
        SubagentRunFilter.Failed => run.Status == StrataAiToolCallStatus.Failed,
        SubagentRunFilter.Stopped => run.Status == StrataAiToolCallStatus.Stopped,
        _ => true,
    };

    private ObservableCollection<SubagentFilterChip> CreateSubagentFilterChips()
    {
        void Select(SubagentRunFilter filter) => SubagentRunFilter = filter;

        return
        [
            new(SubagentRunFilter.All, Loc.Subagent_FilterAll, Select) { IsSelected = true },
            new(SubagentRunFilter.Running, Loc.Subagent_FilterRunning, Select),
            new(SubagentRunFilter.Done, Loc.Subagent_StatusDone, Select),
            new(SubagentRunFilter.Failed, Loc.Subagent_StatusFailed, Select),
            new(SubagentRunFilter.Stopped, Loc.Subagent_StatusStopped, Select),
        ];
    }

    /// <summary>
    /// Rebuilds what the Agents index shows from the ordered runs: the filter chips' counts, the
    /// filtered runs, and — when live and finished runs are both listed — the two section headings.
    /// The row collection is patched in place so a status change moves one row instead of
    /// re-realizing the list.
    /// </summary>
    private void RefreshSubagentIndex()
    {
        var ordered = _orderedSubagentRuns;
        int running = 0, done = 0, failed = 0, stopped = 0;
        foreach (var run in ordered)
        {
            switch (run.Status)
            {
                case StrataAiToolCallStatus.InProgress: running++; break;
                case StrataAiToolCallStatus.Completed: done++; break;
                case StrataAiToolCallStatus.Failed: failed++; break;
                default: stopped++; break;
            }
        }

        foreach (var chip in SubagentFilterChips)
        {
            chip.Count = chip.Filter switch
            {
                SubagentRunFilter.Running => running,
                SubagentRunFilter.Done => done,
                SubagentRunFilter.Failed => failed,
                SubagentRunFilter.Stopped => stopped,
                _ => ordered.Count,
            };
        }

        var statesPresent = (running > 0 ? 1 : 0) + (done > 0 ? 1 : 0) + (failed > 0 ? 1 : 0) + (stopped > 0 ? 1 : 0);
        ShowSubagentFilters = statesPresent > 1;

        // A filter whose runs are all gone (the last running agent finished) falls back to everything;
        // the filter's change handler then refreshes the index.
        var filter = SubagentRunFilter;
        if (filter != SubagentRunFilter.All
            && (!ShowSubagentFilters || SubagentFilterChips.First(chip => chip.Filter == filter).Count == 0))
        {
            SubagentRunFilter = SubagentRunFilter.All;
            return;
        }

        foreach (var chip in SubagentFilterChips)
            chip.IsSelected = chip.Filter == filter;

        var visible = filter == SubagentRunFilter.All
            ? ordered
            : ordered.Where(run => MatchesSubagentFilter(run, filter)).ToList();
        _visibleSubagentRuns = visible;

        // The open run left the filtered view (it finished while "Running" was selected): keep
        // paging through every run rather than losing its place.
        if (SelectedSubagentRun is { } open && !visible.Contains(open))
            _pageAcrossAllRuns = true;

        var rows = new List<object>(visible.Count + 2);
        var finished = visible.Count - (filter == SubagentRunFilter.All ? running : 0);
        if (filter == SubagentRunFilter.All && running > 0 && finished > 0)
        {
            _runningSubagentSection ??= new SubagentIndexSection(Loc.Subagent_SectionRunning, isLive: true);
            _finishedSubagentSection ??= new SubagentIndexSection(Loc.Subagent_SectionFinished, isLive: false);
            _runningSubagentSection.Count = running;
            _finishedSubagentSection.Count = finished;

            rows.Add(_runningSubagentSection);
            rows.AddRange(visible.Take(running));
            rows.Add(_finishedSubagentSection);
            rows.AddRange(visible.Skip(running));
        }
        else
        {
            rows.AddRange(visible);
        }

        SyncSubagentIndexRows(rows);
        NotifySubagentPagerChanged();
    }

    private void SyncSubagentIndexRows(List<object> rows)
    {
        var target = SubagentIndexRows;
        if (target.Count == rows.Count && target.SequenceEqual(rows, ReferenceEqualityComparer.Instance))
            return;

        // Runs are new instances after a transcript rebuild (a chat switch): swap the whole list in
        // one reset instead of hundreds of single-row notifications.
        var kept = new HashSet<object>(target, ReferenceEqualityComparer.Instance);
        var reused = 0;
        foreach (var row in rows)
        {
            if (row is SubagentToolCallItem && kept.Contains(row))
                reused++;
        }

        if (reused == 0 || rows.Count - reused > 8)
        {
            SubagentIndexRows = new ObservableCollection<object>(rows);
            return;
        }

        var wanted = new HashSet<object>(rows, ReferenceEqualityComparer.Instance);
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(target[i]))
                target.RemoveAt(i);
        }

        for (var i = 0; i < rows.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], rows[i]))
                continue;

            var existing = -1;
            for (var j = i + 1; j < target.Count; j++)
            {
                if (ReferenceEquals(target[j], rows[i]))
                {
                    existing = j;
                    break;
                }
            }

            if (existing >= 0)
                target.Move(existing, i);
            else
                target.Insert(i, rows[i]);
        }
    }

    private void NotifySubagentPagerChanged()
    {
        OnPropertyChanged(nameof(HasSubagentRunPager));
        OnPropertyChanged(nameof(SubagentRunPositionText));
        OpenPreviousSubagentRunCommand.NotifyCanExecuteChanged();
        OpenNextSubagentRunCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Re-points a selection at the rebuilt instance of the same run (stable ids survive
    /// transcript rebuilds), so an open Agents page keeps showing the same agent.</summary>
    private SubagentToolCallItem? ResolveReplacementRun(SubagentToolCallItem previous)
        => _transcriptBuilder.SubagentRuns
            .FirstOrDefault(run => string.Equals(run.StableId, previous.StableId, StringComparison.Ordinal));

    /// <summary>Opens one sub-agent's run as a read-only transcript in the Workspace.</summary>
    [RelayCommand]
    private void OpenSubagentRun(SubagentToolCallItem? run)
    {
        if (run is null)
            return;

        _pageAcrossAllRuns = !_visibleSubagentRuns.Contains(run);
        SelectedSubagentRun = run;
        SubagentRunShowRequested?.Invoke();
    }

    /// <summary>Opens the index of every agent in this chat, so running ones are easy to spot.</summary>
    private void ShowSubagentIndex()
    {
        SelectedSubagentRun = null;
        SubagentRunShowRequested?.Invoke();
    }

    /// <summary>Returns from a single run to the full agent index.</summary>
    [RelayCommand]
    private void BackToSubagentIndex() => SelectedSubagentRun = null;

    /// <summary>Clears run inspection state when the surface detaches from a chat.</summary>
    private void ResetSubagentRunState()
    {
        SelectedSubagentRun = null;
        SubagentRunCount = 0;
        RunningSubagentCount = 0;
        SubagentRunFilter = SubagentRunFilter.All;
        _subagentRunRebuild?.CancelPending();
    }

    /// <summary>Releases the run transcript's listeners when the chat surface is torn down.</summary>
    private void DisposeSubagentRunState()
    {
        SelectedSubagentRun = null;
        _subagentRunRebuild?.Dispose();
        _subagentRunRebuild = null;
        _subagentRunTranscript?.Clear();
        _subagentRunTranscript = null;
        SubagentRunTurns = [];
        if (_observedSubagentRunTurns is not null)
        {
            _observedSubagentRunTurns.CollectionChanged -= OnSubagentRunTurnsCollectionChanged;
            _observedSubagentRunTurns = null;
        }
    }
}
