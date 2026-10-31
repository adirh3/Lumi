using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Logging;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Mobile.Layout;
using Lumi.Mobile.Services;
using Lumi.Mobile.ViewModels;
using Lumi.Mobile.Views;
using Lumi.Remote.Protocol;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Mobile.Tests;

/// <summary>
/// A file Lumi made opens in a preview pane of its own, as in the desktop's Workspace: over the chat on
/// a phone, and beside it where two panes fit, taking the chat list's place.
/// </summary>
[Collection("Headless mobile UI")]
public sealed class MobileFilePreviewTests
{
    // ── What a file is ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("chart.PNG", FilePreviewKind.Image)]
    [InlineData("photo.jpeg", FilePreviewKind.Image)]
    [InlineData("report.md", FilePreviewKind.Markdown)]
    [InlineData("Program.cs", FilePreviewKind.Code)]
    [InlineData("settings.json", FilePreviewKind.Code)]
    [InlineData("Dockerfile", FilePreviewKind.Code)]
    [InlineData("CMakeLists.txt", FilePreviewKind.Code)]
    [InlineData("build.log", FilePreviewKind.Text)]
    [InlineData("metrics.csv", FilePreviewKind.Csv)]
    [InlineData("metrics.tsv", FilePreviewKind.Csv)]
    [InlineData("report.pdf", FilePreviewKind.Pdf)]
    [InlineData("release-notes.docx", FilePreviewKind.Unsupported)]
    [InlineData("bundle.zip", FilePreviewKind.Unsupported)]
    [InlineData("LICENSE", FilePreviewKind.Unknown)]
    public void FilesAreRecognisedAsTheDesktopPreviewsThem(string fileName, FilePreviewKind kind) =>
        Assert.Equal(kind, FilePreviewContent.Classify(fileName));

    [Fact]
    public void SourceFilesNameTheirHighlightingGrammar()
    {
        Assert.Equal("cs", FilePreviewContent.CodeLanguage("Program.cs"));
        Assert.Equal("tsx", FilePreviewContent.CodeLanguage("App.TSX"));
        Assert.Equal("dockerfile", FilePreviewContent.CodeLanguage("Dockerfile"));
        Assert.Null(FilePreviewContent.CodeLanguage("notes.txt"));
    }

    [Fact]
    public async Task TextIsReadWithinItsBoundAndBinaryFilesAreRefused()
    {
        using var files = new TempFiles();

        var longText = await FilePreviewText.ReadAsync(
            files.Write("long.txt", Encoding.UTF8.GetBytes(new string('a', FilePreviewContent.MaxCharacters + 10))),
            CancellationToken.None);
        Assert.NotNull(longText);
        Assert.True(longText.IsTruncated);
        Assert.Equal(FilePreviewContent.MaxCharacters, longText.Text.Length);

        var withBom = await FilePreviewText.ReadAsync(
            files.Write("bom.txt", [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("héllo\r\n\tworld")]),
            CancellationToken.None);
        Assert.Equal("héllo\r\n\tworld", withBom!.Text);
        Assert.False(withBom.IsTruncated);

        Assert.Null(await FilePreviewText.ReadAsync(files.Write("nul.txt", [0x48, 0x00, 0x49]), CancellationToken.None));
        Assert.Null(await FilePreviewText.ReadAsync(files.Write("bad.txt", [0x48, 0xC3, 0x28]), CancellationToken.None));
        Assert.Equal("", (await FilePreviewText.ReadAsync(files.Write("empty.txt", []), CancellationToken.None))!.Text);
    }

    [Fact]
    public void SizesReadAsThePhoneShowsThem()
    {
        Assert.Equal("940 B", FilePreviewContent.FormatSize(940));
        Assert.Equal("12 KB", FilePreviewContent.FormatSize(12 * 1024));
        Assert.Equal("1.4 MB", FilePreviewContent.FormatSize(1_468_006));
        Assert.Equal("25 MB", FilePreviewContent.FormatSize(25L * 1024 * 1024));
    }

    // ── Tables ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CsvTablesReadQuotedFieldsAndLineUpNumbers()
    {
        var table = CsvPreviewTable.Parse(
            "Name,Notes,Score\nAda,\"Likes \"\"tea\"\", and cake\",98.5\n\"Grace\nHopper\",Admiral,100\nLinus,,7",
            isTruncated: false);

        Assert.Equal(new[] { "Name", "Notes", "Score" }, table.Columns.Select(column => column.Header));
        Assert.Equal(new[] { false, false, true }, table.Columns.Select(column => column.IsNumeric));
        Assert.Equal(3, table.Rows.Count);
        Assert.Equal("Likes \"tea\", and cake", table.Rows[0].Cells[1].Text);
        Assert.Equal("Grace Hopper", table.Rows[1].Cells[0].Text);
        Assert.Equal("", table.Rows[2].Cells[1].Text);
        Assert.Equal(new[] { false, true, false }, table.Rows.Select(row => row.IsAlternate));
        Assert.Equal(3, table.Rows[2].Number);
        Assert.Equal("3 rows · 3 columns", table.SummaryText);
        Assert.False(table.IsLimited);
        Assert.All(table.Columns, column => Assert.InRange(column.Width, 64, 280));
        Assert.Equal(table.RowNumberWidth + table.Columns.Sum(column => column.Width), table.Width);
    }

    [Fact]
    public void CsvTablesFindTheirSeparatorAndFillRaggedRows()
    {
        var semicolons = CsvPreviewTable.Parse("Land;Wert\nDeutschland;1,5\nÖsterreich;2,25\n", false);
        Assert.Equal(new[] { "Land", "Wert" }, semicolons.Columns.Select(column => column.Header));
        Assert.Equal("1,5", semicolons.Rows[0].Cells[1].Text);

        var excel = CsvPreviewTable.Parse("sep=|\r\nA|B\r\n1|2\r\n", false);
        Assert.Equal(new[] { "A", "B" }, excel.Columns.Select(column => column.Header));
        Assert.Single(excel.Rows);

        var ragged = CsvPreviewTable.Parse("a,b\n1\n2,3,4\n", false);
        Assert.Equal(new[] { "a", "b", "Column 3" }, ragged.Columns.Select(column => column.Header));
        Assert.Equal(new[] { "1", "", "" }, ragged.Rows[0].Cells.Select(cell => cell.Text));

        var tabs = CsvPreviewTable.Parse("a,b\tc\n1,2\t3\n", false, '\t');
        Assert.Equal(new[] { "a,b", "c" }, tabs.Columns.Select(column => column.Header));
    }

    [Fact]
    public void CsvTablesStopAtTheirBoundAndNeverShowAHalfReadRow()
    {
        var rows = new StringBuilder("n,square\n");
        for (var i = 1; i <= CsvPreviewTable.MaxRows + 5; i++)
            rows.Append(i).Append(',').Append(i * i).Append('\n');
        var large = CsvPreviewTable.Parse(rows.ToString(), false);
        Assert.Equal(CsvPreviewTable.MaxRows, large.Rows.Count);
        Assert.True(large.IsLimited);

        var cut = CsvPreviewTable.Parse("a,b\n1,2\n3,4", isTruncated: true);
        Assert.Single(cut.Rows);
        Assert.True(cut.IsLimited);

        var wide = CsvPreviewTable.Parse(
            string.Join(',', Enumerable.Range(1, 65).Select(i => $"c{i}")) + "\n"
            + string.Join(',', Enumerable.Range(1, 65)) + "\n",
            false);
        Assert.Equal(CsvPreviewTable.MaxColumns, wide.Columns.Count);
        Assert.True(wide.IsLimited);

        var empty = CsvPreviewTable.Parse("", false);
        Assert.False(empty.HasRows);
        Assert.Empty(empty.Columns);
        Assert.False(empty.IsLimited);
    }

    [Fact]
    public async Task CsvFilesAreReadByTheDesktopsRulesAndADamagedOneSaysSo()
    {
        // The rules the desktop reads a CSV by: a blank first line, a quoted header holding commas.
        var desktopRules = CsvPreviewRecords.Parse("\r\n\"Name, with commas\";\"Price\"\nA;10", false);
        var table = CsvPreviewTable.Parse("\r\n\"Name, with commas\";\"Price\"\nA;10", false);
        Assert.Equal(';', desktopRules.Delimiter);
        Assert.Equal(new[] { "Name, with commas", "Price" }, table.Columns.Select(column => column.Header));
        Assert.Equal(new[] { "A", "10" }, table.Rows.Single().Cells.Select(cell => cell.Text));
        Assert.True(table.Columns[1].IsNumeric);

        var malformed = Assert.Throws<CsvPreviewFormatException>(() => CsvPreviewTable.Parse("Name,Notes\nA,\"unfinished", false));
        Assert.Equal(2, malformed.LineNumber);

        using var files = new TempFiles();
        var preview = Preview("broken.csv", Serve(files.Write("broken.csv", "sep=,\nName,Notes\nA,\"closed\"unexpected\n"u8.ToArray())));
        await preview.LoadAsync();
        Assert.True(preview.HasError);
        Assert.Equal("This file is damaged, or isn't what its name says.", preview.ErrorText);
        Assert.False(preview.ShowCsv);
    }

    // ── The preview ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FilesThePhoneCannotDrawAreNeverDownloadedJustToSaySo()
    {
        using var files = new TempFiles();
        var path = files.Write("release-notes.docx", [0x50, 0x4B, 0x03, 0x04]);
        var downloads = 0;
        var preview = Preview("release-notes.docx", _ =>
        {
            downloads++;
            return Task.FromResult(new ProducedFileDownload(path, null));
        });

        await preview.LoadAsync();

        Assert.Equal(FilePreviewKind.Unsupported, preview.Kind);
        Assert.True(preview.ShowUnsupported);
        Assert.False(preview.ShowLoading);
        Assert.False(preview.ShowContentActions);
        Assert.Equal(0, downloads);
        Assert.Equal("DOCX", preview.Subtitle);

        // Open, Share and Save fetch it once and share that copy.
        Assert.Equal(path, (await preview.EnsureDownloadedAsync()).Path);
        Assert.Equal(path, (await preview.EnsureDownloadedAsync()).Path);
        Assert.Equal(1, downloads);
        Assert.Equal("DOCX · 4 B", preview.Subtitle);
    }

    [Fact]
    public async Task OneDownloadServesThePreviewAndEveryAction()
    {
        using var files = new TempFiles();
        var path = files.Write("report.md", "# Fold report\n\nAll postures pass."u8.ToArray());
        var arrival = new TaskCompletionSource<ProducedFileDownload>(TaskCreationOptions.RunContinuationsAsynchronously);
        var downloads = 0;
        var preview = Preview("report.md", _ =>
        {
            downloads++;
            return arrival.Task;
        });

        var loading = preview.LoadAsync();
        var action = preview.EnsureDownloadedAsync();
        Assert.True(preview.ShowLoading);
        Assert.False(preview.ShowContentActions);

        arrival.SetResult(new ProducedFileDownload(path, null));
        await loading;
        Assert.Equal(path, (await action).Path);
        Assert.Equal(1, downloads);
        Assert.True(preview.ShowMarkdown);
        Assert.True(preview.ShowContentActions);
        Assert.False(preview.ShowLoading);
        Assert.Equal("# Fold report\n\nAll postures pass.", preview.Text);
        Assert.Equal($"MD · {preview.SizeText}", preview.Subtitle);
    }

    [Fact]
    public async Task APreviewKeepsItsCopyUntilTheLastViewLetsGo()
    {
        using var files = new TempFiles();
        var path = files.Write("build.log", "ok"u8.ToArray());
        var preview = Preview("build.log", Serve(path));
        await preview.LoadAsync();
        Assert.True(preview.ShowText);

        preview.AttachPresenter();
        preview.Close();
        Assert.True(preview.IsClosed);
        Assert.True(File.Exists(path), "a pane still sliding away keeps showing its file");
        Assert.Equal("ok", preview.Text);

        preview.DetachPresenter();
        Assert.False(File.Exists(path));
        Assert.Null((await preview.EnsureDownloadedAsync()).Path);
    }

    [Fact]
    public async Task AFileThatArrivesAfterThePreviewClosedIsThrownAway()
    {
        using var files = new TempFiles();
        var path = files.Write("late.md", "late"u8.ToArray());
        var arrival = new TaskCompletionSource<ProducedFileDownload>(TaskCreationOptions.RunContinuationsAsynchronously);
        var token = CancellationToken.None;
        var preview = Preview("late.md", cancellationToken =>
        {
            token = cancellationToken;
            return arrival.Task;
        });

        var loading = preview.LoadAsync();
        preview.Close();
        Assert.True(token.IsCancellationRequested, "closing stops the download");

        arrival.SetResult(new ProducedFileDownload(path, null));
        await loading;
        Assert.False(File.Exists(path));
        Assert.Null(preview.LocalPath);
        Assert.Null(preview.Text);
    }

    [Fact]
    public async Task AFailedDownloadSaysWhyAndRetryTriesAgain()
    {
        using var files = new TempFiles();
        var path = files.Write("notes.txt", "second time lucky"u8.ToArray());
        var attempts = 0;
        var preview = Preview("notes.txt", _ => Task.FromResult(++attempts == 1
            ? new ProducedFileDownload(null, "The computer is asleep.")
            : new ProducedFileDownload(path, null)));

        await preview.LoadAsync();
        Assert.True(preview.HasError);
        Assert.Equal("The computer is asleep.", preview.ErrorText);
        Assert.False(preview.ShowLoading);
        Assert.False(preview.ShowText);
        Assert.False(preview.ShowContentActions);

        await preview.RetryCommand.ExecuteAsync(null);
        Assert.Equal(2, attempts);
        Assert.False(preview.HasError);
        Assert.True(preview.ShowText);
        Assert.Equal("second time lucky", preview.Text);
    }

    [Fact]
    public async Task TextThatIsNotTextOffersAnotherAppAndUnknownNamesReadAsText()
    {
        using var files = new TempFiles();
        var binary = Preview("dump.txt", Serve(files.Write("dump.txt", [0x4C, 0x00, 0x01, 0x02])));
        await binary.LoadAsync();
        Assert.Equal(FilePreviewKind.Unsupported, binary.Kind);
        Assert.True(binary.ShowUnsupported);

        var license = Preview("LICENSE", Serve(files.Write("LICENSE", "MIT License"u8.ToArray())));
        await license.LoadAsync();
        Assert.Equal(FilePreviewKind.Text, license.Kind);
        Assert.True(license.ShowText);

        var empty = Preview("empty.md", Serve(files.Write("empty.md", [])));
        await empty.LoadAsync();
        Assert.True(empty.ShowEmpty);
        Assert.False(empty.ShowMarkdown);
        Assert.True(empty.ShowContentActions, "an empty file can still be opened elsewhere");
    }

    [Fact]
    public async Task AFileWaitsBehindItsLoadingTileUntilItsPaneHasLanded()
    {
        using var files = new TempFiles();
        var preview = Preview("huge.md", Serve(files.Write("huge.md",
            Encoding.UTF8.GetBytes(new string('x', FilePreviewContent.MaxCharacters + 1)))));
        preview.IsSettling = true;
        await preview.LoadAsync();
        Assert.False(preview.IsLoading);
        Assert.True(preview.ShowLoading, "the tile keeps breathing while the pane still moves");
        Assert.False(preview.ShowMarkdown);
        Assert.False(preview.ShowContentActions);
        Assert.False(preview.ShowLimitNotice);
        // Nothing reaches a viewer, so nothing is built while the pane moves.
        Assert.Null(preview.MarkdownSections);
        Assert.Null(preview.MarkdownText);

        var changed = new List<string?>();
        preview.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        preview.IsSettling = false;
        Assert.False(preview.ShowLoading);
        Assert.True(preview.ShowMarkdown);
        Assert.True(preview.ShowContentActions);
        Assert.True(preview.ShowLimitNotice);
        Assert.Contains(nameof(FilePreviewViewModel.ShowMarkdown), changed);
        Assert.Contains(nameof(FilePreviewViewModel.ShowLoading), changed);
        Assert.Contains(nameof(FilePreviewViewModel.MarkdownSections), changed);

        // Only the markdown viewer gets the file: its sections, and its whole text for selection.
        Assert.Equal(preview.Text, string.Join("\n", preview.MarkdownSections!.Select(section => section.Text)));
        Assert.Equal(preview.Text, preview.MarkdownText);
        Assert.Null(preview.CodeSections);
        Assert.Null(preview.TextSections);
        Assert.Null(preview.CodeText);
        Assert.Null(preview.PlainText);

        // What has nothing to build says so at once: a failure, or a file the phone cannot draw.
        var failed = Preview("notes.txt", _ => Task.FromResult(new ProducedFileDownload(null, "The computer is asleep.")));
        failed.IsSettling = true;
        await failed.LoadAsync();
        Assert.True(failed.HasError);
        Assert.False(failed.ShowLoading);

        var unsupported = Preview("release-notes.docx", Serve(files.Write("release-notes.docx", [0x50, 0x4B])));
        unsupported.IsSettling = true;
        await unsupported.LoadAsync();
        Assert.True(unsupported.ShowUnsupported);
        Assert.False(unsupported.ShowLoading);
    }

    [Fact]
    public async Task LongCodeIsShownPlainAndLongFilesSayTheyAreCut()
    {
        using var files = new TempFiles();
        var small = Preview("Program.cs", Serve(files.Write("Program.cs", "class A { }"u8.ToArray())));
        await small.LoadAsync();
        Assert.True(small.ShowCode);
        Assert.Equal("cs", small.CodeLanguage);

        const string line = "var value = 42; // a line of generated code\n";
        var generated = string.Concat(Enumerable.Repeat(line, FilePreviewContent.MaxHighlightedCharacters / line.Length + 10));
        var large = Preview("Generated.cs", Serve(files.Write("Generated.cs", Encoding.UTF8.GetBytes(generated))));
        await large.LoadAsync();
        Assert.True(large.ShowCode);
        Assert.Null(large.CodeLanguage);
        Assert.False(large.ShowLimitNotice);

        var huge = Preview("huge.txt", Serve(files.Write("huge.txt",
            Encoding.UTF8.GetBytes(new string('x', FilePreviewContent.MaxCharacters + 1)))));
        await huge.LoadAsync();
        Assert.True(huge.ShowLimitNotice);
        Assert.Equal("Showing the beginning of this file. Open it in another app to see all of it.", huge.LimitNoticeText);
    }

    [Fact]
    public async Task CsvFilesBecomeATableThatSaysHowLongItIs()
    {
        using var files = new TempFiles();
        var preview = Preview("metrics.csv", Serve(files.Write("metrics.csv", "Device,Width\nFold7,984\nPixel,412\n"u8.ToArray())));
        await preview.LoadAsync();

        Assert.True(preview.ShowCsv);
        Assert.Equal(2, preview.Table!.Rows.Count);
        Assert.Equal($"CSV · 2 rows · {preview.SizeText}", preview.Subtitle);
        Assert.False(preview.ShowLimitNotice);
    }

    [Fact]
    public async Task PdfsNeedARendererAndLongOnesSayWhatTheyShow()
    {
        using var files = new TempFiles();
        var path = files.Write("report.pdf", "%PDF-1.4"u8.ToArray());
        var downloads = 0;
        var withoutRenderer = new FilePreviewViewModel(Attachment("report.pdf"), Guid.NewGuid(), _ =>
        {
            downloads++;
            return Task.FromResult(new ProducedFileDownload(path, null));
        }, new FakePdfRenderer(1) { Available = false });
        await withoutRenderer.LoadAsync();
        Assert.True(withoutRenderer.ShowUnsupported);
        Assert.Equal(0, downloads);

        var renderer = new FakePdfRenderer(250);
        var preview = new FilePreviewViewModel(Attachment("report.pdf"), Guid.NewGuid(), Serve(path), renderer);
        await preview.LoadAsync();

        Assert.True(preview.ShowPdf);
        Assert.Equal(PdfPreviewLimits.MaxPages, preview.Pdf!.Pages.Count);
        Assert.True(preview.ShowLimitNotice);
        Assert.Equal("Showing the first 200 pages. Open the file in another app to see the rest.", preview.LimitNoticeText);
        Assert.Equal($"PDF · 250 pages · {preview.SizeText}", preview.Subtitle);
        Assert.Equal("Page 1 of 250", preview.Pdf.Pages[0].AccessibleName);
        Assert.Equal(842d / 595, preview.Pdf.Pages[0].AspectRatio, 3);

        preview.AttachPresenter();
        preview.Close();
        Assert.False(renderer.Document!.IsDisposed);
        preview.DetachPresenter();
        Assert.True(renderer.Document.IsDisposed);
        Assert.False(File.Exists(path));
    }

    // ── The chat's preview ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OpeningAFileReplacesThePreviewAndTheSameFileStaysOpen()
    {
        using var files = new TempFiles();
        var sink = new PreviewSink(files);
        var chat = new MobileChatViewModel(sink);
        chat.Reset(Guid.NewGuid(), "Files");
        var report = Attachment("report.md");

        chat.OpenFilePreviewCommand.Execute(report);
        var first = Assert.IsType<FilePreviewViewModel>(chat.FilePreview);
        Assert.True(chat.IsFilePreviewOpen);
        await WaitUntilAsync(() => first.ShowMarkdown);
        Assert.Equal(chat.ChatId, sink.LastChatId);
        Assert.Equal(report.MessageId, sink.LastMessageId);

        chat.OpenFilePreviewCommand.Execute(report);
        Assert.Same(first, chat.FilePreview);
        // A file the user attached has no message of Lumi's to fetch it from.
        chat.OpenFilePreviewCommand.Execute(new RemoteAttachment { FileName = "my-photo.png", Extension = ".png" });
        Assert.Same(first, chat.FilePreview);
        Assert.Equal(1, sink.Downloads);

        chat.OpenFilePreviewCommand.Execute(Attachment("release-notes.docx"));
        Assert.True(first.IsClosed);
        Assert.False(File.Exists(first.LocalPath), "nothing showed the replaced preview, so its copy went at once");
        Assert.Equal("release-notes.docx", chat.FilePreview!.FileName);
        Assert.Equal(1, sink.Downloads);
    }

    [Fact]
    public void APreviewBelongsToItsChatAndGivesWayToTheChatsOtherSurfaces()
    {
        using var files = new TempFiles();
        var chat = new MobileChatViewModel(new PreviewSink(files));
        chat.Reset(Guid.NewGuid(), "Files");
        chat.OpenFilePreviewCommand.Execute(Attachment("release-notes.docx"));
        var preview = chat.FilePreview!;

        chat.Reset(Guid.NewGuid(), "Another chat");
        Assert.Null(chat.FilePreview);
        Assert.True(preview.IsClosed);

        chat.OpenFilePreviewCommand.Execute(Attachment("release-notes.docx"));
        Assert.True(chat.DismissTopmostSheet());
        Assert.False(chat.IsFilePreviewOpen);
        Assert.False(chat.DismissTopmostSheet());

        // Beside the chat there is room for one supporting surface, as on the desktop.
        chat.UseSidePanels = true;
        chat.IsPlanOpen = true;
        chat.OpenFilePreviewCommand.Execute(Attachment("release-notes.docx"));
        Assert.False(chat.IsPlanOpen);
        chat.IsSourcesSheetOpen = true;
        Assert.False(chat.IsFilePreviewOpen);

        chat.OpenFilePreviewCommand.Execute(Attachment("release-notes.docx"));
        chat.IsGitChangesOpen = true;
        Assert.False(chat.IsFilePreviewOpen);
        chat.OpenFilePreviewCommand.Execute(Attachment("release-notes.docx"));
        Assert.False(chat.IsGitChangesOpen);
        Assert.True(chat.IsFilePreviewOpen);
    }

    // ── Where the pane goes ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnOnePaneThePreviewCoversTheChatAndBackReturnsToIt()
    {
        await using var scope = new ShellScope();
        var shell = scope.Paired(412, 892);
        shell.Chat.Reset(Guid.NewGuid(), "Phone");
        shell.IsDrawerOpen = false;

        shell.Chat.OpenFilePreviewCommand.Execute(Attachment("release-notes.docx"));
        var preview = shell.Chat.FilePreview!;

        Assert.True(shell.IsFilePreviewCoveringChat);
        Assert.False(shell.IsFilePreviewBeside);
        Assert.False(shell.CanDragDrawer, "a swipe across a file must not pull the chat list over it");
        Assert.False(shell.CanAutomaticallyRecoverGatewaySignIn);
        Assert.True(shell.CanGoBack);

        shell.GoBackCommand.Execute(null);
        Assert.False(shell.Chat.IsFilePreviewOpen);
        Assert.True(preview.IsClosed);
        Assert.False(shell.IsDrawerOpen, "Back from a file returns to its chat, not to the list");
        Assert.True(shell.CanDragDrawer);
    }

    [Theory]
    [InlineData(984, 1092, 413, 571)] // Galaxy Z Fold7, open
    [InlineData(750, 832, 360, 390)] // Fold7 open at its large display size
    [InlineData(1280, 800, 520, 760)] // a large tablet, where the list would otherwise stay
    public async Task OnTwoPanesTheChatTakesTheListsPlaceAndTheFileOpensBesideIt(
        double width, double height, double chat, double pane)
    {
        await using var scope = new ShellScope();
        var shell = scope.Paired(width, height);
        shell.Chat.Reset(Guid.NewGuid(), "Fold");
        Assert.True(shell.IsDrawerDocked);

        shell.Chat.OpenFilePreviewCommand.Execute(Attachment("release-notes.docx"));

        Assert.True(shell.IsFilePreviewBeside);
        Assert.False(shell.IsFilePreviewCoveringChat);
        Assert.False(shell.IsDrawerDocked, "the chat takes the list's place");
        Assert.Equal(0, shell.NavigationLeadingWidth);
        Assert.Equal(chat, shell.FilePreviewChatWidth);
        Assert.Equal(0, shell.FilePreviewGapWidth);
        Assert.Equal(pane, shell.FilePreviewPaneWidth);
        Assert.True(shell.CanGoBack);
        Assert.False(shell.CanDragDrawer);

        shell.Chat.CloseFilePreviewCommand.Execute(null);
        Assert.True(shell.IsDrawerDocked, "the list comes back when the file closes");
        Assert.False(shell.IsSidebarCollapsed);
    }

    [Theory]
    [InlineData(884, 908, 24, 430, 430)] // a physical hinge: nothing is drawn under it
    [InlineData(984, 1092, 0, 492, 492)] // a half-folded crease: the chat and the file meet at it
    public async Task OnAFoldTheChatAndTheFileMeetAtTheFold(
        double width, double height, double hingeSize, double hingePosition, double pane)
    {
        await using var scope = new ShellScope();
        var shell = scope.Shell;
        shell.IsPaired = true;
        shell.UpdateLayout(width, height, FoldPosture.BookVerticalHinge, hingeSize, hingePosition);
        shell.Chat.Reset(Guid.NewGuid(), "Book");
        Assert.Equal(hingeSize > 0, shell.ReservesHingeForNavigation);

        shell.Chat.OpenFilePreviewCommand.Execute(Attachment("release-notes.docx"));

        Assert.True(shell.IsFilePreviewBeside);
        Assert.False(shell.ReservesHingeForNavigation, "the chat, not the list, sits before the fold");
        Assert.Equal(0, shell.NavigationLeadingWidth);
        Assert.Equal(hingePosition, shell.FilePreviewChatWidth);
        Assert.Equal(hingeSize, shell.FilePreviewGapWidth);
        Assert.Equal(pane, shell.FilePreviewPaneWidth);

        shell.Chat.CloseFilePreviewCommand.Execute(null);
        Assert.Equal(hingeSize > 0, shell.ReservesHingeForNavigation);
        Assert.True(shell.IsDrawerDocked);
    }

    [Fact]
    public async Task AskingForTheListBackTradesTheFileForIt()
    {
        await using var scope = new ShellScope();
        var shell = scope.Paired(984, 1092);
        shell.Chat.Reset(Guid.NewGuid(), "Fold");
        shell.Chat.OpenFilePreviewCommand.Execute(Attachment("release-notes.docx"));
        Assert.False(shell.IsDrawerDocked);

        shell.ToggleDrawerCommand.Execute(null);

        Assert.True(shell.IsDrawerDocked);
        Assert.False(shell.Chat.IsFilePreviewOpen);
        Assert.False(shell.IsSidebarCollapsed);
    }

    [Fact]
    public async Task FoldingKeepsTheFileInViewAndAnotherPlaceLeavesItBehind()
    {
        await using var scope = new ShellScope();
        var shell = scope.Paired(984, 1092);
        shell.Chat.Reset(Guid.NewGuid(), "Fold");
        shell.Chat.OpenFilePreviewCommand.Execute(Attachment("release-notes.docx"));
        Assert.True(shell.IsFilePreviewBeside);

        shell.UpdateLayout(412, 960);
        Assert.True(shell.IsFilePreviewCoveringChat, "the file stays in view over the chat on the cover screen");
        Assert.False(shell.IsDrawerOpen);

        shell.UpdateLayout(984, 1092);
        Assert.True(shell.IsFilePreviewBeside);
        Assert.False(shell.IsDrawerDocked);

        shell.SelectPlace(MobilePlace.Settings);
        Assert.False(shell.Chat.IsFilePreviewOpen);
        shell.SelectPlace(MobilePlace.Chats);
        Assert.True(shell.IsDrawerDocked);
    }

    // ── On screen ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public Task EveryKindOfFileShowsInItsOwnViewWithItsActions() =>
        RunViewAsync(async (view, window, logs) =>
        {
            using var files = new TempFiles();

            async Task<FilePreviewViewModel> ShowAsync(string name, byte[] content, IPdfPreviewRenderer? renderer = null)
            {
                var preview = new FilePreviewViewModel(Attachment(name), Guid.NewGuid(), Serve(files.Write(name, content)), renderer);
                view.DataContext = preview;
                await preview.LoadAsync();
                await SettleAsync(window);
                return preview;
            }

            var markdown = await ShowAsync("report.md", "# Report\n\nAll **ten** postures pass."u8.ToArray());
            AssertShows(view, "PreviewMarkdownScroll");
            Assert.Equal("report.md", Find<TextBlock>(view, "PreviewTitle").Text);
            Assert.Equal(markdown.Subtitle, Find<TextBlock>(view, "PreviewSubtitle").Text);
            Assert.True(Find<Control>(view, "PreviewActionsBar").IsEffectivelyVisible);
            AssertTouchTarget(Find<Button>(view, "PreviewOpenButton"), window);
            Assert.Equal("Open with", Find<Button>(view, "PreviewOpenButton").GetValue(Avalonia.Automation.AutomationProperties.NameProperty));
            Assert.False(Find<Button>(view, "PreviewShareButton").IsVisible, "this host has no share sheet");
            Assert.True(Find<Button>(view, "PreviewSaveButton").IsVisible);

            await ShowAsync("ComposerInsetTests.cs", "class ComposerInsetTests { }"u8.ToArray());
            AssertShows(view, "PreviewCodeScroll");
            Assert.Equal("cs", Find<ItemsControl>(view, "PreviewCode").GetVisualDescendants().OfType<StrataCodeBlock>().Single().Language);

            await ShowAsync("build.log", "Build succeeded."u8.ToArray());
            AssertShows(view, "PreviewTextScroll");

            var csv = await ShowAsync("metrics.csv", "Device,Width\nFold7,984\nPixel,412\n"u8.ToArray());
            AssertShows(view, "PreviewCsvScroll");
            Assert.Equal(csv.Table!.Width, Find<DockPanel>(view, "PreviewCsvTable").Bounds.Width, 1);
            var fold = Assert.Single(view.GetVisualDescendants().OfType<CsvRowView>(), row => row.CellTexts.Contains("Fold7"));
            Assert.True(fold.IsEffectivelyVisible && fold.Bounds.Width >= csv.Table.Width - 1);
            Assert.Equal(["Fold7", "984"], fold.CellTexts);
            Assert.Equal("1: Fold7, 984", fold.GetValue(Avalonia.Automation.AutomationProperties.NameProperty));

            await ShowAsync("fold-resume-after.png", ReadAsset("avares://Lumi.Mobile/Assets/lumi-icon.png"));
            AssertShows(view, "PreviewImage");

            var renderer = new FakePdfRenderer(3);
            var pdf = await ShowAsync("fold-layout-report.pdf", "%PDF-1.4"u8.ToArray(), renderer);
            AssertShows(view, "PreviewPdfScroll");
            await SettleAsync(window, () => pdf.Pdf!.Pages[0].Image is not null);
            Assert.Contains(renderer.Document!.Renders, render => render.Page == 0 && render.Width > 300);

            var unsupported = await ShowAsync("Lumi-1.0-release-notes.docx", [0x50, 0x4B, 0x03, 0x04]);
            AssertShows(view, "PreviewUnsupportedState");
            Assert.False(Find<Control>(view, "PreviewActionsBar").IsEffectivelyVisible);
            AssertTouchTarget(Find<Button>(view, "PreviewUnsupportedOpenButton"), window);
            Assert.Null(unsupported.LocalPath);

            var failed = new FilePreviewViewModel(Attachment("notes.txt"), Guid.NewGuid(),
                _ => Task.FromResult(new ProducedFileDownload(null, "The computer is asleep.")));
            view.DataContext = failed;
            await failed.LoadAsync();
            await SettleAsync(window);
            AssertShows(view, "PreviewErrorState");
            Assert.Equal("The computer is asleep.", Find<TextBlock>(view, "PreviewErrorText").Text);
            AssertTouchTarget(Find<Button>(view, "PreviewRetryButton"), window);

            // Letting go of a PDF gives its pages back before the document closes.
            view.DataContext = null;
            await SettleAsync(window);
            Assert.All(pdf.Pdf!.Pages, page => Assert.Null(page.Image));
            AssertNoBindingErrors(logs);
        });

    [Theory]
    [InlineData("report")]
    [InlineData("prose")]
    [InlineData("hebrew")]
    public async Task ALongFileReadsInItsSectionsExactlyAsItDoesWhole(string document)
    {
        var text = document switch
        {
            "report" => FilePreviewSectionsTests.Report(sections: 6),
            "prose" => FilePreviewSectionsTests.Prose(sections: 12),
            _ => FilePreviewSectionsTests.Hebrew(paragraphs: 40)
        };
        using var files = new TempFiles();
        using var session = HeadlessMobileSession.Start();
        ExceptionDispatchInfo? failure = null;
        await session.Dispatch(async () =>
        {
            var preview = new FilePreviewViewModel(Attachment("report.md"), Guid.NewGuid(), Serve(files.Write("report.md", Encoding.UTF8.GetBytes(text))));
            var view = new FilePreviewView { DataContext = preview };
            var whole = new StrataMarkdown { Markdown = text, IsInline = true, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
            // Tall enough that every section is built, beside the same file built whole.
            var window = new Window { Width = 412, Height = 16000, Content = new Grid { Children = { view, whole } } };
            try
            {
                window.Show();
                await preview.LoadAsync();
                ItemsControl? Sections() => view.GetVisualDescendants().OfType<ItemsControl>().SingleOrDefault(control => control.Name == "PreviewMarkdown");
                await SettleAsync(window, () => Sections() is { ItemCount: > 0 } built && built.ItemCount == preview.MarkdownSections?.Count);
                var list = Find<ItemsControl>(view, "PreviewMarkdown");
                Assert.True(list.ItemCount > 2, $"{list.ItemCount} sections");
                whole.Width = list.Bounds.Width;
                await SettleAsync(window);

                var sections = list.GetVisualDescendants().OfType<StrataMarkdown>().ToList();
                Assert.Equal(list.ItemCount, sections.Count);
                Assert.All(sections, section => Assert.Equal(whole.FontSize, section.FontSize));
                // The sections stack to the whole file's height, the last keeping the space after a block. Each
                // rounds its height to a pixel on its own; a seam that showed would be a line or a block gap off.
                Assert.Equal(whole.Bounds.Height + StrataMarkdown.BlockSpacing, list.Bounds.Height, sections.Count * 0.5);
                Assert.Equal(preview.Text, preview.MarkdownText);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
        failure?.Throw();
    }

    [Fact]
    public Task OnAPhoneTheFileCoversTheChatAndBackSlidesItAway() =>
        RunShellAsync(412, 892, async (shell, view, window, logs) =>
        {
            using var files = new TempFiles();
            var preview = Show(shell, files, "keyboard-gap-report.md", "# Keyboard gap\n\nFixed on **resume**."u8.ToArray());
            await preview.LoadAsync();
            await SettleAsync(window, () => !shell.IsFilePreviewMoving && preview.ShowMarkdown);

            Assert.True(shell.IsFilePreviewCoveringChat);
            var host = Find<Panel>(view, "FilePreviewHost");
            var conversation = Find<Border>(view, "ConversationFrame");
            var pane = Find<FilePreviewView>(view, "FilePreviewPane");
            Assert.True(host.IsVisible);
            Assert.Equal(2, Grid.GetColumn(host));
            Assert.Equal(conversation.Bounds, host.Bounds);
            Assert.Same(preview, pane.DataContext);
            Assert.False(ChatDetailView.ShouldShowNativeComposerEditor(shell), "the native editor would draw over the file");

            var back = Find<Button>(view, "PreviewBackButton");
            Assert.False(Find<Button>(view, "PreviewCloseButton").IsVisible);
            AssertTouchTarget(back, window);
            Assert.True(Find<Control>(view, "PreviewMarkdown").IsEffectivelyVisible);
            var actions = Find<Border>(view, "PreviewActions");
            Assert.True(actions.TranslatePoint(new Point(0, actions.Bounds.Height), window)!.Value.Y <= window.Height - 15);
            AssertTouchTarget(Find<Button>(view, "PreviewOpenButton"), window);

            Tap(window, back);
            Assert.False(shell.Chat.IsFilePreviewOpen);
            Assert.True(host.IsVisible, "the file slides away rather than vanishing");
            Assert.True(File.Exists(preview.LocalPath), "the sliding pane still shows its file");
            await SettleAsync(window, () => !host.IsVisible);
            Assert.False(File.Exists(preview.LocalPath));
            Assert.Null(pane.DataContext);
            Assert.False(shell.IsDrawerOpen, "Back from a file returns to its chat");

            // A file opened while the last is still leaving takes over the same pane.
            var first = Show(shell, files, "first.md", "# First"u8.ToArray());
            await first.LoadAsync();
            await SettleAsync(window, () => !shell.IsFilePreviewMoving);
            Tap(window, back);
            var second = Show(shell, files, "second.txt", "Second"u8.ToArray());
            Assert.True(first.IsClosed);
            Assert.False(File.Exists(first.LocalPath));
            await second.LoadAsync();
            await SettleAsync(window, () => !shell.IsFilePreviewMoving && second.ShowText);
            Assert.True(host.IsVisible);
            Assert.Same(second, pane.DataContext);
            Assert.Equal(conversation.Bounds, host.Bounds);
            AssertNoBindingErrors(logs);
        });

    [Theory]
    [InlineData(412, 892)]
    [InlineData(984, 1092)]
    public Task AFileIsBuiltOnlyOnceItsPaneHasLanded(double width, double height) =>
        RunShellAsync(width, height, async (shell, view, window, logs) =>
        {
            using var files = new TempFiles();
            var preview = Show(shell, files, "keyboard-gap-report.md", "# Keyboard gap\n\nFixed on **resume**."u8.ToArray());
            await preview.LoadAsync();
            Dispatcher.UIThread.RunJobs();
            // The file is on the phone already, yet the opening pane carries only its loading tile.
            Assert.True(shell.IsFilePreviewMoving);
            Assert.True(preview.IsSettling);
            var pane = Find<FilePreviewView>(view, "FilePreviewPane");
            Assert.True(Find<Control>(pane, "PreviewLoadingState").IsEffectivelyVisible);
            Assert.False(Find<Control>(pane, "PreviewMarkdownScroll").IsEffectivelyVisible);

            await SettleAsync(window, () => !shell.IsFilePreviewMoving && !shell.IsDrawerMoving);
            Assert.False(preview.IsSettling);
            await SettleAsync(window);
            AssertShows(pane, "PreviewMarkdownScroll");
            AssertNoBindingErrors(logs);
        });

    [Fact]
    public Task AnOpeningInterruptedByAFoldStillShowsTheFile() =>
        RunShellAsync(984, 1092, async (shell, view, window, logs) =>
        {
            using var files = new TempFiles();
            var preview = Show(shell, files, "report.md", "# Report"u8.ToArray());
            await preview.LoadAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.True(preview.IsSettling);

            // Folding shut while the pane grows lands it over the chat at once.
            window.Width = 412;
            window.Height = 960;
            await SettleAsync(window, () => shell.IsFilePreviewCoveringChat && !shell.IsFilePreviewMoving);
            Assert.False(preview.IsSettling);
            await SettleAsync(window);
            AssertShows(Find<FilePreviewView>(view, "FilePreviewPane"), "PreviewMarkdownScroll");

            // Another file replacing one that is still settling waits for the pane too.
            shell.Chat.CloseFilePreviewCommand.Execute(null);
            await SettleAsync(window, () => !Find<Panel>(view, "FilePreviewHost").IsVisible);
            var first = Show(shell, files, "first.md", "# First"u8.ToArray());
            await first.LoadAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.True(shell.IsFilePreviewMoving);
            Assert.True(first.IsSettling);
            var second = Show(shell, files, "second.md", "# Second"u8.ToArray());
            await second.LoadAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.True(second.IsSettling);
            Assert.False(first.IsSettling);
            await SettleAsync(window, () => !shell.IsFilePreviewMoving);
            Assert.False(second.IsSettling);
            await SettleAsync(window);
            AssertShows(Find<FilePreviewView>(view, "FilePreviewPane"), "PreviewMarkdownScroll");
            AssertNoBindingErrors(logs);
        });

    [Theory]
    [InlineData(984, 1092, FoldPosture.Flat, 0, 0, 413)]
    [InlineData(884, 908, FoldPosture.BookVerticalHinge, 24, 430, 430)]
    [InlineData(984, 1092, FoldPosture.BookVerticalHinge, 0, 492, 492)]
    public Task OnTwoPanesTheChatMovesLeftAndTheFileOpensOnTheRight(
        double width, double height, FoldPosture posture, double hingeSize, double hingePosition, double chat) =>
        RunShellAsync(width, height, async (shell, view, window, logs) =>
        {
            Assert.True(shell.IsDrawerDocked);
            var conversation = Find<Border>(view, "ConversationFrame");
            var listEdge = X(conversation, window);
            Assert.True(listEdge > 300, "the chat list sits on the leading side before a file opens");

            using var files = new TempFiles();
            var preview = Show(shell, files, "fold-layout-metrics.csv", "Device,Width\nFold7,984\nPixel,412\n"u8.ToArray());
            await preview.LoadAsync();
            await SettleAsync(window, () => !shell.IsFilePreviewMoving && !shell.IsDrawerMoving && preview.ShowCsv);

            var host = Find<Panel>(view, "FilePreviewHost");
            var pane = width - chat - hingeSize;
            Assert.True(shell.IsFilePreviewBeside);
            Assert.False(shell.IsDrawerDocked);
            Assert.Equal(4, Grid.GetColumn(host));
            Assert.Equal(0, X(conversation, window), 1);
            Assert.Equal(chat, conversation.Bounds.Width, 1);
            Assert.Equal(chat + hingeSize, X(host, window), 1);
            Assert.Equal(pane, host.Bounds.Width, 1);
            Assert.Equal(hingeSize, Find<Border>(view, "FilePreviewHingeGap").Bounds.Width, 1);
            Assert.True(ChatDetailView.ShouldShowNativeComposerEditor(shell), "the chat beside a file stays usable");

            var close = Find<Button>(view, "PreviewCloseButton");
            Assert.False(Find<Button>(view, "PreviewBackButton").IsVisible);
            AssertTouchTarget(close, window);
            Assert.True(X(close, window) + close.Bounds.Width <= width - 8, "✕ sits inside the floating card");
            Assert.True(Find<DockPanel>(view, "PreviewCsvTable").IsEffectivelyVisible);

            Tap(window, close);
            Assert.False(shell.Chat.IsFilePreviewOpen);
            await SettleAsync(window, () => !host.IsVisible && !shell.IsFilePreviewMoving && !shell.IsDrawerMoving);
            Assert.True(shell.IsDrawerDocked, "the list returns to its place");
            Assert.Equal(listEdge, X(conversation, window), 1);
            Assert.False(File.Exists(preview.LocalPath));
            AssertNoBindingErrors(logs);
        }, posture, hingeSize, hingePosition);

    [Fact]
    public Task FoldingShutKeepsTheFileInViewAndUnfoldingPutsItBack() =>
        RunShellAsync(984, 1092, async (shell, view, window, logs) =>
        {
            using var files = new TempFiles();
            var preview = Show(shell, files, "report.md", "# Report"u8.ToArray());
            await preview.LoadAsync();
            await SettleAsync(window, () => !shell.IsFilePreviewMoving && !shell.IsDrawerMoving && preview.ShowMarkdown);
            var host = Find<Panel>(view, "FilePreviewHost");
            var conversation = Find<Border>(view, "ConversationFrame");

            window.Width = 412;
            window.Height = 960;
            await SettleAsync(window, () => shell.IsFilePreviewCoveringChat && !shell.IsDrawerMoving);
            Assert.Equal(2, Grid.GetColumn(host));
            Assert.Equal(conversation.Bounds, host.Bounds);
            Assert.True(Find<Button>(view, "PreviewBackButton").IsVisible);
            Assert.False(shell.IsDrawerOpen);

            window.Width = 984;
            window.Height = 1092;
            await SettleAsync(window, () => shell.IsFilePreviewBeside && !shell.IsDrawerMoving);
            Assert.Equal(4, Grid.GetColumn(host));
            Assert.Equal(413, conversation.Bounds.Width, 1);
            Assert.Equal(571, host.Bounds.Width, 1);
            Assert.True(Find<Button>(view, "PreviewCloseButton").IsVisible);
            Assert.True(File.Exists(preview.LocalPath));
            AssertNoBindingErrors(logs);
        });

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private static RemoteAttachment Attachment(string fileName) => new()
    {
        FileName = fileName,
        Extension = Path.GetExtension(fileName),
        MessageId = Guid.NewGuid()
    };

    private static FilePreviewViewModel Preview(
        string fileName, Func<CancellationToken, Task<ProducedFileDownload>> download) =>
        new(Attachment(fileName), Guid.NewGuid(), download, new FakePdfRenderer(1) { Available = false });

    private static Func<CancellationToken, Task<ProducedFileDownload>> Serve(string path) =>
        _ => Task.FromResult(new ProducedFileDownload(path, null));

    /// <summary>Opens a file in the shell's preview as a tap on its chip would, served from a local copy.</summary>
    private static FilePreviewViewModel Show(MobileShellViewModel shell, TempFiles files, string name, byte[] content)
    {
        var path = files.Write(name, content);
        var preview = new FilePreviewViewModel(Attachment(name), shell.Chat.ChatId, Serve(path));
        var previous = shell.Chat.FilePreview;
        shell.Chat.FilePreview = preview;
        previous?.Close();
        return preview;
    }

    private static byte[] ReadAsset(string uri)
    {
        using var asset = AssetLoader.Open(new Uri(uri));
        using var copy = new MemoryStream();
        asset.CopyTo(copy);
        return copy.ToArray();
    }

    private static readonly string[] ContentStates =
    [
        "PreviewImage", "PreviewMarkdownScroll", "PreviewCodeScroll", "PreviewTextScroll", "PreviewCsvScroll",
        "PreviewPdfScroll", "PreviewLoadingState", "PreviewEmptyState", "PreviewErrorState", "PreviewUnsupportedState"
    ];

    /// <summary>Exactly one of the pane's states shows.</summary>
    private static void AssertShows(FilePreviewView view, string name)
    {
        foreach (var state in ContentStates)
            Assert.True(Find<Control>(view, state).IsEffectivelyVisible == (state == name), $"{state} visibility while showing {name}");
    }

    private static void AssertTouchTarget(Control control, Window window)
    {
        Assert.True(control.IsEffectivelyVisible, control.Name);
        Assert.True(control.Bounds.Width >= 48 && control.Bounds.Height >= 48,
            $"{control.Name} is {control.Bounds.Width}×{control.Bounds.Height}");
        var origin = control.TranslatePoint(default, window)!.Value;
        Assert.True(origin.X >= 0 && origin.Y >= 0
                    && origin.X + control.Bounds.Width <= window.Width + 0.5
                    && origin.Y + control.Bounds.Height <= window.Height + 0.5, $"{control.Name} at {origin}");
    }

    private static double X(Visual visual, Window window) => visual.TranslatePoint(default, window)!.Value.X;

    private static void AssertNoBindingErrors(BindingLogSink logs)
    {
        lock (logs.Messages)
            Assert.True(logs.Messages.Count == 0, string.Join(Environment.NewLine, logs.Messages.Distinct()));
    }

    private static T Find<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    /// <summary>A real tap at the control's centre, so hit testing decides what receives it.</summary>
    private static void Tap(Window window, Control control)
    {
        var centre = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        var hit = Assert.IsAssignableFrom<Visual>(window.InputHitTest(centre));
        Assert.True(ReferenceEquals(hit, control) || hit.GetVisualAncestors().Contains(control), $"{control.Name} is covered");
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var waited = 0; waited < 3000 && !condition(); waited += 10)
            await Task.Delay(10);
        Assert.True(condition());
    }

    /// <summary>Lets layout, render frames and timers run until the condition holds (or a short quiet spell).</summary>
    private static async Task SettleAsync(Window window, Func<bool>? until = null, int timeoutMs = 4000)
    {
        var clock = Stopwatch.StartNew();
        var frames = 0;
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(16);
            if (until is null ? ++frames >= 10 : until())
                break;
        }

        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        if (until is not null)
            Assert.True(until(), "The preview did not settle in time.");
    }

    private static async Task RunViewAsync(Func<FilePreviewView, Window, BindingLogSink, Task> test)
    {
        using var session = HeadlessMobileSession.Start();
        ExceptionDispatchInfo? failure = null;
        await session.Dispatch(async () =>
        {
            var previousLogger = Logger.Sink;
            var view = new FilePreviewView();
            var logs = new BindingLogSink(source => source is Visual visual
                && (ReferenceEquals(visual, view) || visual.GetVisualAncestors().Contains(view)));
            var window = new Window { Width = 412, Height = 892, Content = view };
            try
            {
                Logger.Sink = logs;
                window.Show();
                await test(view, window, logs);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                window.Close();
                Logger.Sink = previousLogger;
            }
        }, CancellationToken.None);
        failure?.Throw();
    }

    private static async Task RunShellAsync(
        double width,
        double height,
        Func<MobileShellViewModel, MobileShellView, Window, BindingLogSink, Task> test,
        FoldPosture posture = FoldPosture.Flat,
        double hingeSize = 0,
        double hingePosition = 0)
    {
        using var session = HeadlessMobileSession.Start();
        ExceptionDispatchInfo? failure = null;
        await session.Dispatch(async () =>
        {
            var previousLogger = Logger.Sink;
            MobileShellViewModel? shell = null;
            Window? window = null;
            try
            {
                shell = new MobileShellViewModel(store: session.NewStore(), post: action =>
                {
                    if (Dispatcher.UIThread.CheckAccess())
                        action();
                    else
                        Dispatcher.UIThread.Post(action);
                });
                shell.IsPaired = true;
                shell.IsConnected = true;
                shell.IsHostReady = true;
                shell.HostName = "Preview PC";
                shell.Chat.Reset(Guid.NewGuid(), "Fold layout check");
                var view = new MobileShellView
                {
                    DataContext = shell,
                    Posture = posture,
                    HingeSize = hingeSize,
                    HingePosition = hingePosition
                };
                var logs = new BindingLogSink(source => source is Visual visual
                    && visual.GetVisualAncestors().OfType<Control>().Any(control => control.Name == "FilePreviewHost"));
                Logger.Sink = logs;
                window = new Window { Width = width, Height = height, Content = view };
                window.Show();
                await SettleAsync(window);
                if (!shell.Layout.CanShowTwoPanes)
                {
                    // A newly paired phone opens on its chat list; reading the chat steps it aside.
                    shell.IsDrawerOpen = false;
                    await SettleAsync(window, () => !shell.IsDrawerMoving);
                }

                await test(shell, view, window, logs);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                window?.Close();
                Logger.Sink = previousLogger;
                if (shell is not null)
                    await shell.DisposeAsync();
            }
        }, CancellationToken.None);
        failure?.Throw();
    }

    private sealed class TempFiles : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "lumi-file-preview-tests", Guid.NewGuid().ToString("N"));

        /// <summary>Writes a file under its own folder, so the same name can be written twice.</summary>
        public string Write(string name, byte[] content)
        {
            var folder = Path.Combine(_root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, name);
            File.WriteAllBytes(path, content);
            return path;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                    Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class ShellScope : IAsyncDisposable
    {
        private readonly string _directory =
            Path.Combine(Path.GetTempPath(), "lumi-mobile-tests", Guid.NewGuid().ToString("n"));

        public ShellScope() =>
            Shell = new MobileShellViewModel(store: new MobileSettingsStore(_directory), post: action => action());

        public MobileShellViewModel Shell { get; }

        public MobileShellViewModel Paired(double width, double height)
        {
            Shell.IsPaired = true;
            Shell.UpdateLayout(width, height);
            return Shell;
        }

        public async ValueTask DisposeAsync()
        {
            await Shell.DisposeAsync();
            try
            {
                if (Directory.Exists(_directory))
                    Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>The PC, as far as a preview is concerned: it hands over the files Lumi made.</summary>
    private sealed class PreviewSink(TempFiles files) : IRemoteCommandSink, IRemoteProducedFileSink
    {
        public int Downloads { get; private set; }

        public Guid LastChatId { get; private set; }

        public Guid LastMessageId { get; private set; }

        public Task<ProducedFileDownload> DownloadProducedFileAsync(
            Guid chatId, Guid messageId, string fileName, CancellationToken cancellationToken)
        {
            Downloads++;
            LastChatId = chatId;
            LastMessageId = messageId;
            return Task.FromResult(new ProducedFileDownload(files.Write(fileName, "# Report"u8.ToArray()), null));
        }

        public Task<RemoteCommandResult> SendCommandAsync(RemoteCommand command) =>
            Task.FromResult(new RemoteCommandResult { Ok = true });

        public Task<RemoteUploadResponse> UploadAsync(string fileName, ReadOnlyMemory<byte> content) =>
            throw new NotSupportedException();
    }

    private sealed class FakePdfRenderer(int pages) : IPdfPreviewRenderer
    {
        public bool Available { get; init; } = true;

        public bool IsAvailable => Available;

        public FakePdfDocument? Document { get; private set; }

        public Task<IPdfPreviewDocument> OpenAsync(string path, CancellationToken cancellationToken)
        {
            Document = new FakePdfDocument(pages);
            return Task.FromResult<IPdfPreviewDocument>(Document);
        }
    }

    private sealed class FakePdfDocument(int pages) : IPdfPreviewDocument
    {
        public int PageCount => Math.Min(pages, PdfPreviewLimits.MaxPages);

        public int TotalPageCount => pages;

        public List<(int Page, int Width)> Renders { get; } = [];

        public bool IsDisposed { get; private set; }

        public Size GetPageSize(int pageIndex) => new(595, 842);

        public Task<Bitmap> RenderPageAsync(int pageIndex, int pixelWidth, CancellationToken cancellationToken)
        {
            Renders.Add((pageIndex, pixelWidth));
            return Task.FromResult<Bitmap>(new WriteableBitmap(
                new PixelSize(pixelWidth, (int)Math.Round(pixelWidth * 842d / 595)),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Premul));
        }

        public void Dispose() => IsDisposed = true;
    }

    /// <summary>Binding warnings raised by the preview's own controls.</summary>
    private sealed class BindingLogSink(Func<object?, bool> isPreview) : ILogSink
    {
        public List<string> Messages { get; } = [];

        public bool IsEnabled(LogEventLevel level, string area) =>
            area == LogArea.Binding && level >= LogEventLevel.Warning;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
            Record(level, area, source, messageTemplate);

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate,
            params object?[] propertyValues) =>
            Record(level, area, source, messageTemplate + " " + string.Join(" ", propertyValues));

        private void Record(LogEventLevel level, string area, object? source, string message)
        {
            if (!IsEnabled(level, area) || !Dispatcher.UIThread.CheckAccess() || !isPreview(source))
                return;
            lock (Messages)
                Messages.Add($"{source?.GetType().Name}: {message}");
        }
    }
}
