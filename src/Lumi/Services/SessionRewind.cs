#pragma warning disable GHCP001 // The SDK's history rewind API is experimental; Lumi uses it only here.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Lumi.Models;

namespace Lumi.Services;

/// <summary>What going back to before a turn could put back: the files the agent changed since.</summary>
/// <param name="IsBusy">True when work still running in the session keeps the files from being read now.</param>
internal sealed record RewindFilesPreview(IReadOnlyList<RewindFile> Files, bool IsBusy = false)
{
    public static readonly RewindFilesPreview None = new([]);

    /// <summary>The session the preview was read from.</summary>
    public string? SessionId { get; init; }

    /// <summary>The <c>user.message</c> event of the turn the preview went back to before.</summary>
    public string? TurnEventId { get; init; }
}

/// <summary>What going back did: whether the session's history was cut, and what happened to the files when they were asked for.</summary>
internal readonly record struct SessionRewindResult(bool HistoryRewound, RewindFilesOutcome? Files);

/// <summary>
/// Going back to before one of a Copilot session's user turns, through the runtime's history API.
/// The session captures every file its agent changes (<see cref="SessionConfigBase.EnableFileChangeTracking"/>,
/// from its first turn): going back can put those files back as they were, skipping any the person
/// changed since. The runtime owns the captures and the restore; this maps its answers onto Lumi's.
/// </summary>
internal static class SessionRewind
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>The files going back to before the turn that starts at <paramref name="turnEventId"/> would put back.</summary>
    public static async Task<RewindFilesPreview> PreviewFilesAsync(
        CopilotSession session, string turnEventId, CancellationToken ct)
        => ToPreview(await session.Rpc.History.PreviewRewindAsync(turnEventId, ct).ConfigureAwait(false))
            with { SessionId = session.SessionId, TurnEventId = turnEventId };

    /// <summary>
    /// Discards the turn that starts at <paramref name="turnEventId"/> and everything after it. With
    /// <paramref name="restore"/> (the preview the person took), the files the agent changed in those
    /// turns are put back first, as previewed: only when the preview went back to this same turn of this
    /// same session. Since then the chat may have moved to another session (a send that replaced it, say),
    /// where the preview no longer says what putting files back would do, so they stay as they are. The
    /// conversation is cut even when the files can't be put back, as an edit's always has been, so only
    /// a session that refuses both is reported as not rewound.
    /// </summary>
    public static async Task<SessionRewindResult> RewindAsync(
        CopilotSession session, string turnEventId, RewindFilesPreview? restore, CancellationToken ct)
    {
        var restoreFiles = restore is not null
            && string.Equals(restore.SessionId, session.SessionId, StringComparison.Ordinal)
            && string.Equals(restore.TurnEventId, turnEventId, StringComparison.Ordinal)
                ? restore.Files
                : null;
        HistoryRewindResult? result = null;
        Exception? failure = null;
        try
        {
            result = await session.Rpc.History.RewindAsync(
                turnEventId,
                restoreFiles is null ? HistoryRewindMode.Conversation : HistoryRewindMode.ConversationAndFiles,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // An older runtime, a turn the runtime does not count as a user turn, or a lost connection.
            failure = ex;
        }

        var files = restore is null ? null : DescribeFiles();
        if (result is not null && RewoundHistory(result.Outcome))
            return new SessionRewindResult(true, files);

        try
        {
            await session.Rpc.History.TruncateAsync(turnEventId, ct).ConfigureAwait(false);
            return new SessionRewindResult(true, files);
        }
        catch
        {
            return new SessionRewindResult(false, files);
        }

        RewindFilesOutcome DescribeFiles()
        {
            if (restoreFiles is null)
                return RewindFilesOutcome.NotRestored(RewindFilesStatus.Unavailable);
            return result is null
                ? RewindFilesOutcome.NotRestored(RewindFilesStatus.Failed, failure?.Message)
                : ToOutcome(result, restoreFiles);
        }
    }

    internal static RewindFilesPreview ToPreview(HistoryPreviewRewindResult preview)
    {
        if (!preview.Available)
            return preview.Reason == HistoryRewindUnavailableReason.SessionBusy
                ? new RewindFilesPreview([], IsBusy: true)
                : RewindFilesPreview.None;

        return new RewindFilesPreview((preview.Files ?? [])
            .Where(static file => !string.IsNullOrWhiteSpace(file.Path))
            .Select(static file => new RewindFile(file.Path, ToChange(file.ChangeType), file.LinesAdded, file.LinesRemoved))
            .ToList());
    }

    /// <summary>
    /// What a conversation-and-files rewind did to the files, described as <paramref name="previewed"/>
    /// showed them (a file the preview did not list is described as modified).
    /// </summary>
    internal static RewindFilesOutcome ToOutcome(HistoryRewindResult result, IReadOnlyList<RewindFile> previewed)
    {
        var byPath = new Dictionary<string, RewindFile>(PathComparer);
        foreach (var file in previewed)
            byPath.TryAdd(file.Path, file);

        RewindFile Describe(string path)
            => byPath.TryGetValue(path, out var file) ? file : new RewindFile(path, RewindFileChange.Modified);

        var restored = (result.RestoredFiles ?? [])
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(Describe)
            .ToList();
        var kept = (result.SkippedFiles ?? [])
            .Where(static skipped => !string.IsNullOrWhiteSpace(skipped.Path))
            .Select(skipped => new RewindKeptFile(
                Describe(skipped.Path),
                skipped.Reason == HistoryFileRestoreSkipReason.UserModified
                    ? RewindKeptReason.ChangedSince
                    : RewindKeptReason.NoCopy))
            .ToList();
        return new RewindFilesOutcome(ToStatus(result.Outcome), restored, kept, result.Error);
    }

    /// <summary>True when the outcome says the conversation was cut back to before the turn.</summary>
    internal static bool RewoundHistory(HistoryRewindOutcome outcome)
        => outcome == HistoryRewindOutcome.Success
           || outcome == HistoryRewindOutcome.CheckpointCleanupFailed
           || outcome == HistoryRewindOutcome.SnapshotPruneFailed;

    private static RewindFilesStatus ToStatus(HistoryRewindOutcome outcome)
    {
        if (outcome == HistoryRewindOutcome.Success
            || outcome == HistoryRewindOutcome.CheckpointCleanupFailed
            || outcome == HistoryRewindOutcome.SnapshotPruneFailed
            // The files were put back before the conversation failed to be cut.
            || outcome == HistoryRewindOutcome.TruncationFailed)
            return RewindFilesStatus.Restored;
        if (outcome == HistoryRewindOutcome.SessionBusy)
            return RewindFilesStatus.Busy;
        if (outcome == HistoryRewindOutcome.FileChangeTrackingDisabled
            || outcome == HistoryRewindOutcome.UnsupportedRemoteSession)
            return RewindFilesStatus.Unavailable;
        if (outcome == HistoryRewindOutcome.RollbackIncomplete)
            return RewindFilesStatus.Incomplete;

        // Rolled back, or an outcome this Lumi doesn't know: claim nothing was put back.
        return RewindFilesStatus.Failed;
    }

    private static RewindFileChange ToChange(HistoryRewindChangeType change)
        => change == HistoryRewindChangeType.Created ? RewindFileChange.Created
            : change == HistoryRewindChangeType.Deleted ? RewindFileChange.Deleted
            : RewindFileChange.Modified;
}
