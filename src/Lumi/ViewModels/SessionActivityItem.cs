using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHub.Copilot.Rpc;
using Lumi.Services;

namespace Lumi.ViewModels;

public enum SessionActivityKind
{
    Command,
    Agent,
    Task,
}

/// <summary>
/// One task still running in the session after the assistant finished: a background command, a
/// delegated agent or a client-owned task. It says what the task is doing right now and keeps its
/// newest output; an agent also links to its run, so its row opens the run on the Agents page.
/// Updated in place on every poll, so an expanded row stays open while its output streams in.
/// </summary>
public sealed partial class SessionActivityItem : ObservableObject
{
    /// <summary>How many of the newest lines an expanded row shows.</summary>
    internal const int OutputTailLines = 12;

    /// <summary>Longer lines are cut, so one minified blob cannot cost a huge layout pass.</summary>
    private const int MaxLineLength = 300;

    /// <summary>Only the end of a large output buffer is scanned for its newest lines.</summary>
    private const int MaxScannedOutputLength = 16_384;

    public SessionActivityItem(string id, SessionActivityKind kind)
    {
        Id = id;
        Kind = kind;
    }

    public string Id { get; }

    public SessionActivityKind Kind { get; }

    /// <summary>The tool call that launched an agent, which identifies its run in the transcript.</summary>
    internal string? ToolCallId { get; init; }

    /// <summary>What the task is for: its description, or the command when it has none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Initial))]
    private string _title = "";

    /// <summary>Who runs it: the agent's name, or the kind of task.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Initial), nameof(AccentIndex))]
    private string _owner = "";

    /// <summary>The command line, the agent's model, or a task's longer description.</summary>
    [ObservableProperty] private string _detail = "";

    [ObservableProperty] private DateTimeOffset _startedAt;

    /// <summary>What the task is doing right now: its newest output line, intent or progress message.</summary>
    [ObservableProperty] private string? _activity;

    /// <summary>The newest output (commands) or activity lines (agents, tasks), shown when expanded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutput), nameof(CanExpand))]
    [NotifyCanExecuteChangedFor(nameof(ActivateCommand))]
    private string? _output;

    /// <summary>Completion percentage a task reported; negative while unknown.</summary>
    [ObservableProperty] private double _progressValue = -1;

    [ObservableProperty] private bool _isExpanded;

    /// <summary>The agent's run in this chat's transcript. When known, the row opens it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRun), nameof(CanExpand), nameof(Presented))]
    [NotifyCanExecuteChangedFor(nameof(ActivateCommand))]
    private SubagentToolCallItem? _run;

    /// <summary>What the row renders: the agent's live run when known, otherwise this task.</summary>
    public object Presented => (object?)Run ?? this;

    public bool IsCommand => Kind == SessionActivityKind.Command;
    public bool HasOutput => !string.IsNullOrWhiteSpace(Output);
    public bool HasRun => Run is not null;

    /// <summary>A row with no run to open expands to show its newest output instead.</summary>
    public bool CanExpand => Run is null && HasOutput;

    /// <summary>
    /// Avatar glyph: a prompt for commands, the agent's initial for agents, and the task's own initial
    /// for client tasks, whose owner is only the generic kind.
    /// </summary>
    public string Initial => Kind switch
    {
        SessionActivityKind.Command => ">_",
        SessionActivityKind.Agent => FirstLetter(Owner) ?? FirstLetter(Title) ?? "\u2022",
        _ => FirstLetter(Title) ?? "\u2022",
    };

    /// <summary>The stable colour this kind of agent wears in the chat and the Workspace.</summary>
    public int AccentIndex => SubagentToolCallItem.AccentIndexFor(Owner);

    partial void OnOutputChanged(string? value) => CloseUnlessExpandable();

    partial void OnRunChanged(SubagentToolCallItem? value) => CloseUnlessExpandable();

    /// <summary>
    /// Only a row with output of its own stays open: once its output is gone, or the row now opens a
    /// run instead, an open row would be an empty box its disabled button could no longer close.
    /// </summary>
    private void CloseUnlessExpandable()
    {
        if (!CanExpand)
            IsExpanded = false;
    }

    /// <summary>Opens the agent's run, or shows and hides the task's newest output.</summary>
    [RelayCommand(CanExecute = nameof(CanActivate))]
    private void Activate()
    {
        if (Run is { } run)
        {
            run.OpenRunCommand.Execute(null);
            return;
        }

        IsExpanded = !IsExpanded;
    }

    private bool CanActivate() => Run is not null || HasOutput;

    /// <summary>Adopts a newer snapshot of the same task while keeping what the reader has open.</summary>
    internal void UpdateFrom(SessionActivityItem snapshot)
    {
        Title = snapshot.Title;
        Owner = snapshot.Owner;
        Detail = snapshot.Detail;
        StartedAt = snapshot.StartedAt;
    }

    /// <summary>
    /// Applies the SDK's progress snapshot: the live line and the newest output it reports. A missing
    /// snapshot (a transient failure) keeps the last known state.
    /// </summary>
    internal void ApplyProgress(TaskProgress? progress)
    {
        switch (progress)
        {
            case TaskProgressShell shell:
                var output = OutputLines(shell.RecentOutput);
                Activity = output.Count > 0 ? CleanLine(output[^1]) : null;
                Output = JoinTail(output);
                break;

            case TaskProgressAgent agent:
                var steps = ActivityLines(agent.RecentActivity);
                Activity = CleanLine(agent.LatestIntent) ?? (steps.Count > 0 ? steps[^1] : null);
                Output = JoinTail(steps);
                break;

            case TaskProgressClient client:
                Activity = CleanLine(client.LastMessage) ?? CleanLine(client.Phase);
                ProgressValue = client.Percentage is { } percentage ? Math.Clamp(percentage, 0, 100) : -1;
                Output = JoinTail(ActivityLines(client.RecentActivity));
                break;
        }
    }

    /// <summary>
    /// The visible, non-blank lines of terminal output, indentation kept. Escape sequences are dropped,
    /// and a carriage return overwrites its line as in a terminal, so a progress bar reads as its last
    /// state.
    /// </summary>
    private static List<string> OutputLines(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return [];

        if (output.Length > MaxScannedOutputLength)
            output = output[^MaxScannedOutputLength..];

        var lines = new List<string>();
        foreach (var rawLine in (ToolDisplayHelper.NormalizeDisplayText(output) ?? "").Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var carriageReturn = line.LastIndexOf('\r');
            if (carriageReturn >= 0)
                line = line[(carriageReturn + 1)..];
            line = line.TrimEnd();
            if (!string.IsNullOrWhiteSpace(line))
                lines.Add(Truncate(line));
        }

        return lines;
    }

    private static List<string> ActivityLines(IList<TaskProgressLine>? lines)
        => lines?.Select(line => CleanLine(line.Message)).OfType<string>().ToList() ?? [];

    private static string? CleanLine(string? line)
    {
        line = line?.Trim();
        return string.IsNullOrEmpty(line) ? null : Truncate(line);
    }

    private static string Truncate(string line)
        => line.Length > MaxLineLength ? line[..MaxLineLength] + "\u2026" : line;

    private static string? JoinTail(List<string> lines)
        => lines.Count == 0 ? null : string.Join('\n', lines.Skip(Math.Max(0, lines.Count - OutputTailLines)));

    private static string? FirstLetter(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
                return char.ToUpperInvariant(ch).ToString();
        }

        return null;
    }
}
