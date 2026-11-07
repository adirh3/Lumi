#pragma warning disable GHCP001 // The runtime's rewind answers are built as the SDK's experimental history types.
using System;
using System.Linq;
using GitHub.Copilot.Rpc;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Storage;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// Going back to an earlier message with the files the agent changed since: how the runtime's
/// preview and rewind answers read in Lumi, what the edit bar offers, and the receipt the message
/// sent in place of the edited one carries.
/// </summary>
[Trait("Category", "CoreQA")]
public sealed class SessionRewindTests : IDisposable
{
    private static readonly string Work = OperatingSystem.IsWindows() ? @"C:\work\app" : "/work/app";
    private static readonly string Notes = System.IO.Path.Combine(Work, "docs", "notes.md");
    private static readonly string Program = System.IO.Path.Combine(Work, "src", "Program.cs");
    private static readonly string Old = System.IO.Path.Combine(Work, "old.txt");

    private static readonly RewindFile CreatedNotes = new(Notes, RewindFileChange.Created, 12);
    private static readonly RewindFile ModifiedProgram = new(Program, RewindFileChange.Modified, 4, 2);
    private static readonly RewindFile DeletedOld = new(Old, RewindFileChange.Deleted, 0, 8);

    public SessionRewindTests() => Loc.Load("en");

    public void Dispose() => Loc.Load("en");

    // ── The runtime's answers ───────────────────────────────────────────────

    [Fact]
    public void Preview_ListsWhatTheDiscardedTurnsDidToEachFile()
    {
        var preview = SessionRewind.ToPreview(new HistoryPreviewRewindResult
        {
            Available = true,
            FileCount = 3,
            Files =
            [
                new() { Path = Notes, ChangeType = HistoryRewindChangeType.Created, LinesAdded = 12 },
                new() { Path = Program, ChangeType = HistoryRewindChangeType.Modified, LinesAdded = 4, LinesRemoved = 2 },
                new() { Path = Old, ChangeType = HistoryRewindChangeType.Deleted, LinesRemoved = 8 },
            ],
        });

        Assert.False(preview.IsBusy);
        Assert.Equal([CreatedNotes, ModifiedProgram, DeletedOld], preview.Files);
    }

    [Fact]
    public void Preview_OfABusySession_OffersNothingYetAndSaysWhy()
    {
        var busy = SessionRewind.ToPreview(new HistoryPreviewRewindResult
        {
            Available = false,
            Reason = HistoryRewindUnavailableReason.SessionBusy,
            Files = [],
        });
        var untracked = SessionRewind.ToPreview(new HistoryPreviewRewindResult
        {
            Available = false,
            Reason = HistoryRewindUnavailableReason.FileChangeTrackingDisabled,
            Files = [],
        });

        Assert.True(busy.IsBusy);
        Assert.Empty(busy.Files);
        Assert.False(untracked.IsBusy);
        Assert.Empty(untracked.Files);
    }

    [Fact]
    public void Outcome_DescribesFilesAsPreviewed_AndWhyEachLeftAloneWasKept()
    {
        var mine = System.IO.Path.Combine(Work, "mine.txt");
        var outcome = SessionRewind.ToOutcome(new HistoryRewindResult
        {
            Outcome = HistoryRewindOutcome.Success,
            EventsRemoved = 40,
            RestoredFiles = [Notes, Program],
            SkippedFiles =
            [
                new() { Path = mine, Reason = HistoryFileRestoreSkipReason.UserModified },
                new() { Path = Old, Reason = HistoryFileRestoreSkipReason.SkippedCapture },
            ],
        }, [CreatedNotes, ModifiedProgram, DeletedOld]);

        Assert.Equal(RewindFilesStatus.Restored, outcome.Status);
        Assert.Equal([CreatedNotes, ModifiedProgram], outcome.Restored);
        Assert.Equal(
            [
                new RewindKeptFile(new RewindFile(mine, RewindFileChange.Modified), RewindKeptReason.ChangedSince),
                new RewindKeptFile(DeletedOld, RewindKeptReason.NoCopy),
            ],
            outcome.Kept);
        Assert.Null(outcome.Error);
    }

    [Theory]
    [InlineData("success", true, RewindFilesStatus.Restored)]
    [InlineData("checkpoint-cleanup-failed", true, RewindFilesStatus.Restored)]
    [InlineData("snapshot-prune-failed", true, RewindFilesStatus.Restored)]
    [InlineData("truncation-failed", false, RewindFilesStatus.Restored)]
    [InlineData("files-rolled-back", false, RewindFilesStatus.Failed)]
    [InlineData("rollback-incomplete", false, RewindFilesStatus.Incomplete)]
    [InlineData("session-busy", false, RewindFilesStatus.Busy)]
    [InlineData("file-change-tracking-disabled", false, RewindFilesStatus.Unavailable)]
    [InlineData("unsupported-remote-session", false, RewindFilesStatus.Unavailable)]
    [InlineData("an-outcome-from-a-newer-runtime", false, RewindFilesStatus.Failed)]
    public void Outcome_SaysWhetherTheConversationWasCut_AndWhatHappenedToTheFiles(
        string outcome, bool historyRewound, RewindFilesStatus status)
    {
        var result = new HistoryRewindResult
        {
            Outcome = new HistoryRewindOutcome(outcome),
            RestoredFiles = [],
            SkippedFiles = [],
            Error = historyRewound ? null : "detail",
        };

        Assert.Equal(historyRewound, SessionRewind.RewoundHistory(result.Outcome));
        Assert.Equal(status, SessionRewind.ToOutcome(result, []).Status);
    }

    // ── The edit bar's offer ─────────────────────────────────────────────

    [Fact]
    public void Offer_IsMadeOnlyWhenTheEditCanPutFilesBack_AndSaysWhatHappensToEach()
    {
        Assert.Null(FileRewindOffer.From(RewindFilesPreview.None));
        Assert.Null(FileRewindOffer.From(new RewindFilesPreview([], IsBusy: true)));

        var offer = FileRewindOffer.From(new RewindFilesPreview([CreatedNotes, ModifiedProgram, DeletedOld]))!;

        Assert.Equal("Undo Lumi's changes to 3 files", offer.Label);
        Assert.True(offer.IsChosen);
        Assert.Equal([CreatedNotes, ModifiedProgram, DeletedOld], offer.Files);
        Assert.Equal(["Will be removed", "Will be restored", "Will be brought back"], offer.Rows.Select(static row => row.Status));
        Assert.Equal([TranscriptIcons.Create, TranscriptIcons.Edit, TranscriptIcons.Delete], offer.Rows.Select(static row => row.IconKey));
        Assert.Equal(["notes.md", "Program.cs", "old.txt"], offer.Rows.Select(static row => row.FileName));
        Assert.Equal(("+12", ""), (offer.Rows[0].LinesAdded, offer.Rows[0].LinesRemoved));
        Assert.Equal(("+4", "−2"), (offer.Rows[1].LinesAdded, offer.Rows[1].LinesRemoved));
        Assert.Equal(System.IO.Path.GetDirectoryName(Notes)!.Split(System.IO.Path.DirectorySeparatorChar)[^1],
            offer.Rows[0].DirectoryHint!.Split(System.IO.Path.DirectorySeparatorChar)[^1]);

        Assert.Equal("Undo Lumi's changes to 1 file",
            FileRewindOffer.From(new RewindFilesPreview([ModifiedProgram]))!.Label);
    }

    // ── The receipt ─────────────────────────────────────────────────────────

    [Fact]
    public void Receipt_SaysWhatWasPutBack_AndWhatWasLeftAsItWas()
    {
        Assert.Null(FileRewindReceipt.From(null));
        Assert.Null(FileRewindReceipt.From(new RewindFilesOutcome(RewindFilesStatus.Restored, [], [])));

        var all = FileRewindReceipt.From(new RewindFilesOutcome(RewindFilesStatus.Restored, [CreatedNotes, DeletedOld], []))!;
        Assert.Equal("Undid changes to 2 files", all.Summary);
        Assert.False(all.IsProblem);
        Assert.False(all.HasExplanation);
        Assert.Equal(["Removed", "Brought back"], all.Rows.Select(static row => row.Status));

        var some = FileRewindReceipt.From(new RewindFilesOutcome(
            RewindFilesStatus.Restored,
            [ModifiedProgram],
            [new RewindKeptFile(CreatedNotes, RewindKeptReason.ChangedSince)]))!;
        Assert.Equal("Undid changes to 1 file · 1 kept", some.Summary);
        Assert.False(some.IsProblem);
        Assert.Equal(["Restored", "Kept · you changed it"], some.Rows.Select(static row => row.Status));
        Assert.Equal([false, true], some.Rows.Select(static row => row.IsKept));

        var none = FileRewindReceipt.From(new RewindFilesOutcome(
            RewindFilesStatus.Restored,
            [],
            [
                new RewindKeptFile(CreatedNotes, RewindKeptReason.ChangedSince),
                new RewindKeptFile(ModifiedProgram, RewindKeptReason.NoCopy),
            ]))!;
        Assert.Equal("Left 2 files as they were", none.Summary);
        Assert.Equal(["Kept · you changed it", "Kept · no saved copy"], none.Rows.Select(static row => row.Status));
    }

    [Theory]
    [InlineData(RewindFilesStatus.Busy, "File changes weren't undone", "Lumi was still working in the background, so your files were left as they were.")]
    [InlineData(RewindFilesStatus.Unavailable, "File changes weren't undone", "This chat can't undo file changes, so your files were left as they were.")]
    [InlineData(RewindFilesStatus.Failed, "File changes weren't undone", "Lumi couldn't put your files back, so they were left as they were.")]
    [InlineData(RewindFilesStatus.Incomplete, "File changes partly undone", "Lumi couldn't finish putting your files back. Check the files below.")]
    public void Receipt_ExplainsWhyTheFilesWereNotAllPutBack(RewindFilesStatus status, string summary, string explanation)
    {
        var receipt = FileRewindReceipt.From(new RewindFilesOutcome(status, [ModifiedProgram], [], "  The disk is full.  "))!;

        Assert.True(receipt.IsProblem);
        Assert.Equal(summary, receipt.Summary);
        Assert.Equal(explanation, receipt.Explanation);
        // Only a failure says what the runtime reported, and only an incomplete one lists files.
        Assert.Equal(status is RewindFilesStatus.Failed or RewindFilesStatus.Incomplete ? "The disk is full." : null, receipt.Error);
        Assert.Equal(status == RewindFilesStatus.Incomplete, receipt.HasRows);
    }

    [Fact]
    public void Hebrew_TranslatesTheOfferAndReceipt_ButNotTheFiles()
    {
        Loc.Load("he");

        var offer = FileRewindOffer.From(new RewindFilesPreview([CreatedNotes, ModifiedProgram]))!;
        var receipt = FileRewindReceipt.From(new RewindFilesOutcome(
            RewindFilesStatus.Restored, [CreatedNotes], [new RewindKeptFile(ModifiedProgram, RewindKeptReason.ChangedSince)]))!;

        Assert.Equal("בטל את השינויים של Lumi ב-2 קבצים", offer.Label);
        Assert.Equal(["יימחק", "ישוחזר"], offer.Rows.Select(static row => row.Status));
        Assert.Equal("השינויים בקובץ אחד בוטלו · ללא שינוי: 1", receipt.Summary);
        Assert.Equal(["notes.md", "Program.cs"], receipt.Rows.Select(static row => row.FileName));
        Assert.Equal(Notes, receipt.Rows[0].FilePath);
    }

    // ── On the message ──────────────────────────────────────────────────────

    [Fact]
    public void Receipt_SurvivesTheMessageBeingShownAgain_ButIsNeverSaved()
    {
        var message = new ChatMessage { Role = "user", Content = "Use a table instead." };
        var shown = new ChatMessageViewModel(message);
        Assert.False(shown.HasFileRewind);

        shown.ShowFileRewind(new RewindFilesOutcome(RewindFilesStatus.Restored, [ModifiedProgram], []));

        Assert.True(shown.HasFileRewind);
        Assert.Equal("Undid changes to 1 file", shown.FileRewind!.Summary);
        Assert.Equal("Undid changes to 1 file", new ChatMessageViewModel(message).FileRewind?.Summary);
        Assert.DoesNotContain("Program.cs", ChatStorageJson.SerializeMessage(message));
        Assert.Null(ChatStorageJson.DeserializeMessage(ChatStorageJson.SerializeMessage(message)).FileRewind);
    }
}
