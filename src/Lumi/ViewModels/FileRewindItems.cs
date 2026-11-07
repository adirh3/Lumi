using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;

namespace Lumi.ViewModels;

/// <summary>A file in the edit bar's list of what going back puts back, or in a receipt of what it did.</summary>
public sealed class RewindFileItem
{
    public RewindFileItem(RewindFile file, string status, bool showLines = false, bool isKept = false)
    {
        FilePath = file.Path;
        FileName = ToolDisplayHelper.GetDisplayFileName(file.Path) ?? file.Path;
        DirectoryHint = FileChangeItem.CompactDirectory(Path.GetDirectoryName(file.Path));
        IconKey = file.Change switch
        {
            RewindFileChange.Created => TranscriptIcons.Create,
            RewindFileChange.Deleted => TranscriptIcons.Delete,
            _ => TranscriptIcons.Edit,
        };
        Status = status;
        LinesAdded = showLines && file.LinesAdded > 0 ? $"+{file.LinesAdded}" : "";
        LinesRemoved = showLines && file.LinesRemoved > 0 ? $"−{file.LinesRemoved}" : "";
        IsKept = isKept;
    }

    public string FilePath { get; }
    public string FileName { get; }
    public string? DirectoryHint { get; }
    /// <summary>What the agent did to the file, drawn as the row's line icon.</summary>
    public string IconKey { get; }
    /// <summary>What going back does, or did, to the file.</summary>
    public string Status { get; }
    /// <summary>The lines the agent added, which going back takes away.</summary>
    public string LinesAdded { get; }
    public string LinesRemoved { get; }
    public bool HasLinesAdded => LinesAdded.Length > 0;
    public bool HasLinesRemoved => LinesRemoved.Length > 0;
    public bool HasLines => HasLinesAdded || HasLinesRemoved;
    /// <summary>Left as it is: quieter than the files that were put back.</summary>
    public bool IsKept { get; }
}

/// <summary>
/// The edit bar's offer to put back, with the edit, the files the agent changed after the edited
/// message: shown before anything changes, while the person can still leave the files as they are.
/// </summary>
public sealed partial class FileRewindOffer : ObservableObject
{
    private FileRewindOffer(RewindFilesPreview preview)
    {
        Preview = preview;
        var files = preview.Files;
        Label = files.Count == 1 ? Loc.Rewind_UndoFiles_One : string.Format(Loc.Rewind_UndoFiles_N, files.Count);
        Rows = files.Select(static file => new RewindFileItem(file, file.Change switch
            {
                RewindFileChange.Created => Loc.Rewind_WillRemove,
                RewindFileChange.Deleted => Loc.Rewind_WillBringBack,
                _ => Loc.Rewind_WillRestore,
            }, showLines: true))
            .ToList();
    }

    /// <summary>The runtime's preview the offer shows: the files, and the session and turn they go back to.</summary>
    internal RewindFilesPreview Preview { get; }

    /// <summary>The files as the runtime previewed them, which the rewind's result is described by.</summary>
    internal IReadOnlyList<RewindFile> Files => Preview.Files;

    /// <summary>Whether the edit puts the files back too: chosen unless the person unticks it.</summary>
    [ObservableProperty] private bool _isChosen = true;

    public string Label { get; }
    public IReadOnlyList<RewindFileItem> Rows { get; }

    /// <summary>The offer for a preview, or null when going back has no file to put back.</summary>
    internal static FileRewindOffer? From(RewindFilesPreview preview)
        => preview.Files.Count > 0 ? new FileRewindOffer(preview) : null;
}

/// <summary>
/// What going back did to the files, shown under the message an edit sent in place of the edited
/// one: the files put back, those left as they were and why, or why none was touched.
/// </summary>
public sealed class FileRewindReceipt
{
    private FileRewindReceipt(string summary, bool isProblem, string? explanation, string? error, IReadOnlyList<RewindFileItem> rows)
    {
        Summary = summary;
        IsProblem = isProblem;
        Explanation = explanation;
        Error = string.IsNullOrWhiteSpace(error) ? null : error.Trim();
        Rows = rows;
    }

    public string Summary { get; }
    /// <summary>The files were not all put back as asked.</summary>
    public bool IsProblem { get; }
    public string? Explanation { get; }
    /// <summary>The runtime's own words for a failure, untranslated.</summary>
    public string? Error { get; }
    public IReadOnlyList<RewindFileItem> Rows { get; }
    public bool HasExplanation => Explanation is not null;
    public bool HasError => Error is not null;
    public bool HasRows => Rows.Count > 0;

    /// <summary>The receipt for an outcome, or null when there is nothing to tell (no outcome, or nothing to put back).</summary>
    internal static FileRewindReceipt? From(RewindFilesOutcome? outcome)
    {
        if (outcome is null)
            return null;

        var rows = outcome.Restored
            .Select(static file => new RewindFileItem(file, file.Change switch
            {
                RewindFileChange.Created => Loc.Rewind_Removed,
                RewindFileChange.Deleted => Loc.Rewind_BroughtBack,
                _ => Loc.Rewind_Restored,
            }))
            .Concat(outcome.Kept.Select(static keptFile => new RewindFileItem(
                keptFile.File,
                keptFile.Reason == RewindKeptReason.ChangedSince ? Loc.Rewind_KeptChanged : Loc.Rewind_KeptNoCopy,
                isKept: true)))
            .ToList();

        switch (outcome.Status)
        {
            case RewindFilesStatus.Restored:
                var restored = outcome.Restored.Count;
                var kept = outcome.Kept.Count;
                if (restored == 0 && kept == 0)
                    return null;
                if (restored == 0)
                    return new(kept == 1 ? Loc.Rewind_LeftAsIs_One : string.Format(Loc.Rewind_LeftAsIs_N, kept),
                        isProblem: false, explanation: null, error: null, rows);
                var undid = restored == 1 ? Loc.Rewind_Undid_One : string.Format(Loc.Rewind_Undid_N, restored);
                return new(kept == 0 ? undid : string.Format(Loc.Rewind_UndidWithKept, undid, kept),
                    isProblem: false, explanation: null, error: null, rows);
            case RewindFilesStatus.Incomplete:
                return new(Loc.Rewind_PartlyUndone, isProblem: true, Loc.Rewind_IncompleteDetail, outcome.Error, rows);
            case RewindFilesStatus.Busy:
                return new(Loc.Rewind_NotUndone, isProblem: true, Loc.Rewind_BusyDetail, error: null, []);
            case RewindFilesStatus.Unavailable:
                return new(Loc.Rewind_NotUndone, isProblem: true, Loc.Rewind_UnavailableDetail, error: null, []);
            default:
                return new(Loc.Rewind_NotUndone, isProblem: true, Loc.Rewind_FailedDetail, outcome.Error, []);
        }
    }
}
