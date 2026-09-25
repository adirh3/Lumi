using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Lumi.ViewModels;

/// <summary>One step of a plan as the Workspace overview lists it: a task (checkbox) or a plain bullet.</summary>
public sealed record WorkspacePlanStep(string Text, bool IsDone, bool IsTask = false)
{
    public bool IsBullet => !IsTask;
}

/// <summary>
/// A plan's markdown reduced to what the Workspace shows at a glance: its title, its task progress
/// (when the plan uses <c>- [ ]</c> checkboxes) and the next few steps.
/// </summary>
public sealed partial record WorkspacePlanSummary(
    string? Title,
    string? Summary,
    IReadOnlyList<WorkspacePlanStep> Steps,
    int CompletedCount,
    int TaskCount)
{
    public static WorkspacePlanSummary Empty { get; } = new(null, null, [], 0, 0);

    public bool HasTasks => TaskCount > 0;

    /// <summary>The steps worth showing next: open tasks first, or the plan's first bullets.</summary>
    public IReadOnlyList<WorkspacePlanStep> UpcomingSteps(int count)
    {
        var upcoming = new List<WorkspacePlanStep>(count);
        foreach (var step in Steps)
        {
            if (upcoming.Count == count)
                break;
            if (!HasTasks || !step.IsDone)
                upcoming.Add(step);
        }

        return upcoming;
    }

    public static WorkspacePlanSummary Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return Empty;

        string? title = null;
        string? summary = null;
        var tasks = new List<WorkspacePlanStep>();
        var bullets = new List<WorkspacePlanStep>();
        var inFence = false;

        foreach (var rawLine in markdown.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence || trimmed.Length == 0)
                continue;

            if (HeadingPattern().Match(trimmed) is { Success: true } heading)
            {
                title ??= CleanInline(ClosingHashesPattern().Replace(heading.Groups["text"].Value, ""));
                continue;
            }

            if (TaskPattern().Match(line) is { Success: true } task)
            {
                tasks.Add(new WorkspacePlanStep(
                    CleanInline(task.Groups["text"].Value),
                    task.Groups["mark"].Value is "x" or "X",
                    IsTask: true));
                continue;
            }

            if (BulletPattern().Match(line) is { Success: true } bullet)
            {
                bullets.Add(new WorkspacePlanStep(CleanInline(bullet.Groups["text"].Value), IsDone: false));
                continue;
            }

            if (summary is null && !trimmed.StartsWith('>') && !trimmed.StartsWith('|'))
                summary = Truncate(CleanInline(trimmed), 160);
        }

        var completed = 0;
        foreach (var task in tasks)
        {
            if (task.IsDone)
                completed++;
        }

        return tasks.Count > 0
            ? new WorkspacePlanSummary(title, summary, tasks, completed, tasks.Count)
            : new WorkspacePlanSummary(title, summary, bullets, 0, 0);
    }

    private static string CleanInline(string text)
    {
        var cleaned = LinkPattern().Replace(text, "${label}");
        cleaned = cleaned.Replace("**", "", StringComparison.Ordinal)
            .Replace("__", "", StringComparison.Ordinal)
            .Replace("`", "", StringComparison.Ordinal);
        return cleaned.Trim();
    }

    private static string Truncate(string text, int max)
        => text.Length > max ? text[..max].TrimEnd() + "…" : text;

    [GeneratedRegex(@"^#{1,6}\s+(?<text>.+)$")]
    private static partial Regex HeadingPattern();

    // An ATX heading's optional closing sequence ("## Title ##") must follow whitespace, so "C#" survives.
    [GeneratedRegex(@"\s+#+\s*$")]
    private static partial Regex ClosingHashesPattern();

    [GeneratedRegex(@"^\s*(?:[-*+]|\d+[.)])\s+\[(?<mark>[ xX])\]\s+(?<text>.+)$")]
    private static partial Regex TaskPattern();

    [GeneratedRegex(@"^\s*(?:[-*+]|\d+[.)])\s+(?<text>.+)$")]
    private static partial Regex BulletPattern();

    [GeneratedRegex(@"!?\[(?<label>[^\]]*)\]\([^)]*\)")]
    private static partial Regex LinkPattern();
}
