using System;
using System.Collections.Generic;
using System.Linq;
using Lumi.Models;

namespace Lumi.Services;

/// <summary>
/// How change-only job runs show in a transcript: which runs found nothing new, and which of those
/// carries its job's single status line.
/// </summary>
internal static class BackgroundJobRunPresentation
{
    internal const int MaxStatusLength = 200;

    public static string NormalizeStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return "";

        var normalized = string.Join(' ', status.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= MaxStatusLength
            ? normalized
            : normalized[..(MaxStatusLength - 1)] + "…";
    }

    /// <summary>
    /// The wake messages that show as their job's status line: each status-line job's latest quiet run.
    /// Every other quiet run is hidden.
    /// </summary>
    public static IReadOnlyDictionary<Guid, BackgroundJob> StatusLineRuns(IEnumerable<BackgroundJob> jobs)
    {
        var rows = new Dictionary<Guid, BackgroundJob>();
        foreach (var job in jobs)
        {
            if (job.LastQuietRunMessageId is { } messageId
                && BackgroundJobNoChangeBehaviors.Normalize(job.NoChangeBehavior) == BackgroundJobNoChangeBehaviors.StatusLine)
            {
                rows[messageId] = job;
            }
        }

        return rows;
    }

    /// <summary>
    /// Marks which of <paramref name="messages"/> belong to a quiet job run: its wake message and
    /// everything after it, up to the next user message.
    /// </summary>
    public static bool[] QuietRunMask(IReadOnlyList<ChatMessage> messages)
    {
        var mask = new bool[messages.Count];
        var quiet = false;
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            if (message.Role == "user")
                quiet = message.JobRunQuiet;
            mask[index] = quiet;
        }

        return mask;
    }

    public static bool HasQuietRun(IReadOnlyList<ChatMessage> messages)
        => messages.Any(message => message.JobRunQuiet);
}
