using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using StrataTheme.Controls;

namespace Lumi.ViewModels;

/// <summary>
/// Sub-agent run inspection: the chat-wide index of every agent Lumi delegated to, and the
/// read-only run transcript shown in the right-hand split-view island.
/// </summary>
public partial class ChatViewModel
{
    /// <summary>Raised when a sub-agent run (or the run index) should open in the right island.</summary>
    public event Action? SubagentRunShowRequested;

    /// <summary>Raised to hide the sub-agent run island.</summary>
    public event Action? SubagentRunHideRequested;

    /// <summary>Every sub-agent run in the open chat, oldest first.</summary>
    public ObservableCollection<SubagentToolCallItem> SubagentRuns => _transcriptBuilder.SubagentRuns;

    [ObservableProperty] private bool _isSubagentRunOpen;
    [ObservableProperty] private SubagentToolCallItem? _selectedSubagentRun;
    [ObservableProperty] private int _subagentRunCount;
    [ObservableProperty] private int _runningSubagentCount;

    /// <summary>True while the island shows the index of all runs rather than a single run.</summary>
    public bool IsSubagentIndexVisible => SelectedSubagentRun is null;

    public bool HasSubagentRuns => SubagentRunCount > 0;
    public bool HasRunningSubagents => RunningSubagentCount > 0;

    /// <summary>Header badge text: how many agents are running, or how many finished.</summary>
    public string SubagentRunsSummary => RunningSubagentCount > 0
        ? string.Format(Loc.Subagent_RunningOfTotal, RunningSubagentCount, SubagentRunCount)
        : string.Format(Loc.Subagent_AllFinished, SubagentRunCount);

    partial void OnSelectedSubagentRunChanged(SubagentToolCallItem? value)
        => OnPropertyChanged(nameof(IsSubagentIndexVisible));

    partial void OnSubagentRunCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasSubagentRuns));
        OnPropertyChanged(nameof(SubagentRunsSummary));
    }

    partial void OnRunningSubagentCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasRunningSubagents));
        OnPropertyChanged(nameof(SubagentRunsSummary));
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
        SubagentRunCount = runs.Count;
        RunningSubagentCount = runs.Count(static run => run.Status == StrataAiToolCallStatus.InProgress);

        // Mid-rebuild the collection is only partially populated, so a selection can't be judged
        // missing yet. The builder re-notifies once the rebuild completes.
        if (_transcriptBuilder.IsRebuildingTranscript)
            return;

        if (SelectedSubagentRun is not { } selected || runs.Contains(selected))
            return;

        SelectedSubagentRun = ResolveReplacementRun(selected);
    }

    /// <summary>Re-points a selection at the rebuilt instance of the same run (stable ids survive
    /// transcript rebuilds), so an open run island keeps showing the same agent.</summary>
    private SubagentToolCallItem? ResolveReplacementRun(SubagentToolCallItem previous)
        => _transcriptBuilder.SubagentRuns
            .FirstOrDefault(run => string.Equals(run.StableId, previous.StableId, StringComparison.Ordinal));

    /// <summary>Opens one sub-agent's run as a read-only transcript in the right island.</summary>
    [RelayCommand]
    private void OpenSubagentRun(SubagentToolCallItem? run)
    {
        if (run is null)
            return;

        SelectedSubagentRun = run;
        IsSubagentRunOpen = true;
        SubagentRunShowRequested?.Invoke();
    }

    /// <summary>Opens the index of every agent in this chat, so running ones are easy to spot.</summary>
    private void ShowSubagentIndex()
    {
        SelectedSubagentRun = null;
        IsSubagentRunOpen = true;
        SubagentRunShowRequested?.Invoke();
    }

    /// <summary>Header toggle: opens the agent index, or closes the island when it is already open.</summary>
    [RelayCommand]
    private void ToggleSubagentPanel()
    {
        if (IsSubagentRunOpen)
            CloseSubagentRun();
        else
            ShowSubagentIndex();
    }

    /// <summary>Returns from a single run to the full agent index.</summary>
    [RelayCommand]
    private void BackToSubagentIndex() => SelectedSubagentRun = null;

    [RelayCommand]
    private void CloseSubagentRun()
    {
        IsSubagentRunOpen = false;
        SubagentRunHideRequested?.Invoke();
    }

    /// <summary>Clears run inspection state when the surface detaches from a chat.</summary>
    private void ResetSubagentRunState()
    {
        SelectedSubagentRun = null;
        IsSubagentRunOpen = false;
        SubagentRunCount = 0;
        RunningSubagentCount = 0;
    }
}
