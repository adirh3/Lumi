using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Models;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public class LibraryServiceTests
{
    private static readonly DateTimeOffset Base = new(2026, 7, 20, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ExtractReferences_CapturesSentAttachmentsCreatedFilesAndCitedLinks()
    {
        var chat = new Chat { Id = Guid.NewGuid(), Title = "Trip planning" };
        var sent = Path.Combine(Path.GetTempPath(), "itinerary.pdf");
        var created = Path.Combine(Path.GetTempPath(), "budget.xlsx");

        var messages = new List<ChatMessage>
        {
            new() { Role = "user", Timestamp = Base, Attachments = [sent] },
            new()
            {
                Role = "tool",
                ToolName = "announce_file",
                Timestamp = Base.AddMinutes(1),
                Content = $"{{\"filePath\":\"{created.Replace("\\", "\\\\")}\"}}"
            },
            new()
            {
                Role = "assistant",
                Timestamp = Base.AddMinutes(2),
                Sources = [new SearchSource { Url = "https://example.com/guide", Title = "Guide", Snippet = "How to" }]
            }
        };

        var references = LibraryService.ExtractReferences(chat, messages);

        Assert.Equal(3, references.Count);
        Assert.Contains(references, r => r.Origin == LibraryArtifactOrigin.Sent && r.Name == "itinerary.pdf");
        Assert.Contains(references, r => r.Origin == LibraryArtifactOrigin.Created && r.Name == "budget.xlsx");
        Assert.Contains(references, r => r.Kind == LibraryArtifactKind.Link && r.Location == "https://example.com/guide");
        Assert.All(references, r => Assert.Equal("Trip planning", r.ChatTitle));
    }

    [Fact]
    public void ExtractReferences_IgnoresRelativePathsAndNonHttpLinks()
    {
        var chat = new Chat { Id = Guid.NewGuid(), Title = "Noise" };
        var messages = new List<ChatMessage>
        {
            new() { Role = "user", Timestamp = Base, Attachments = ["relative/report.pdf", "  "] },
            new()
            {
                Role = "assistant",
                Timestamp = Base,
                Sources =
                [
                    new SearchSource { Url = "ftp://example.com/file" },
                    new SearchSource { Url = "not a url" }
                ]
            }
        };

        Assert.Empty(LibraryService.ExtractReferences(chat, messages));
    }

    [Fact]
    public void ExtractReferences_SkipsAnnounceFilePayloadsFromOtherTools()
    {
        var chat = new Chat { Id = Guid.NewGuid(), Title = "Other tool" };
        var path = Path.Combine(Path.GetTempPath(), "not-announced.txt");
        var messages = new List<ChatMessage>
        {
            new()
            {
                Role = "tool",
                ToolName = "write_file",
                Timestamp = Base,
                Content = $"{{\"filePath\":\"{path.Replace("\\", "\\\\")}\"}}"
            }
        };

        Assert.Empty(LibraryService.ExtractReferences(chat, messages));
    }

    [Fact]
    public void Merge_DedupesAcrossChatsKeepingNewestDisplayAndOriginalOrigin()
    {
        var oldChat = Guid.NewGuid();
        var newChat = Guid.NewGuid();
        var path = Path.Combine(Path.GetTempPath(), "shared.png");

        var artifacts = LibraryService.Merge(
        [
            Reference(path, LibraryArtifactOrigin.Created, oldChat, "First chat", Base),
            Reference(path, LibraryArtifactOrigin.Sent, newChat, "Second chat", Base.AddDays(3))
        ], new Dictionary<Guid, string>());

        var artifact = Assert.Single(artifacts);
        Assert.Equal(2, artifact.ChatCount);
        Assert.Equal(LibraryArtifactOrigin.Created, artifact.Origin);
        Assert.Equal(newChat, artifact.ChatId);
        Assert.Equal("Second chat", artifact.ChatTitle);
        Assert.Equal(Base.AddDays(3), artifact.LastSeen);
        Assert.Equal(Base, artifact.FirstSeen);
    }

    [Fact]
    public void Merge_ResolvesProjectNameAndOrdersNewestFirst()
    {
        var projectId = Guid.NewGuid();
        var chatId = Guid.NewGuid();

        var artifacts = LibraryService.Merge(
        [
            Reference(Path.Combine(Path.GetTempPath(), "old.txt"), LibraryArtifactOrigin.Sent, chatId, "Chat", Base),
            Reference(Path.Combine(Path.GetTempPath(), "new.txt"), LibraryArtifactOrigin.Sent, chatId, "Chat", Base.AddHours(5), projectId)
        ], new Dictionary<Guid, string> { [projectId] = "Lumi" });

        Assert.Equal(["new.txt", "old.txt"], artifacts.Select(a => a.Name));
        Assert.Equal("Lumi", artifacts[0].ProjectName);
        Assert.Null(artifacts[1].ProjectName);
    }

    [Fact]
    public void Merge_ReportsExistenceAndSizeForFilesOnDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lumi-library-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "hello library");
        try
        {
            var artifacts = LibraryService.Merge(
                [Reference(path, LibraryArtifactOrigin.Created, Guid.NewGuid(), "Chat", Base)],
                new Dictionary<Guid, string>());

            var artifact = Assert.Single(artifacts);
            Assert.True(artifact.Exists);
            Assert.Equal(new FileInfo(path).Length, artifact.SizeBytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Merge_MarksMissingFilesAsUnavailable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lumi-library-missing-{Guid.NewGuid():N}.txt");

        var artifact = Assert.Single(LibraryService.Merge(
            [Reference(path, LibraryArtifactOrigin.Sent, Guid.NewGuid(), "Chat", Base)],
            new Dictionary<Guid, string>()));

        Assert.False(artifact.Exists);
        Assert.Equal(0, artifact.SizeBytes);
    }

    [Theory]
    [InlineData(".png", LibraryArtifactKind.Image)]
    [InlineData(".PDF", LibraryArtifactKind.Document)]
    [InlineData(".xlsx", LibraryArtifactKind.Sheet)]
    [InlineData(".pptx", LibraryArtifactKind.Slides)]
    [InlineData(".cs", LibraryArtifactKind.Code)]
    [InlineData(".mp4", LibraryArtifactKind.Media)]
    [InlineData(".zip", LibraryArtifactKind.Archive)]
    [InlineData(".unknownext", LibraryArtifactKind.Other)]
    [InlineData("", LibraryArtifactKind.Other)]
    [InlineData(null, LibraryArtifactKind.Other)]
    public void ClassifyExtension_MapsExtensionsOntoBuckets(string? extension, LibraryArtifactKind expected)
        => Assert.Equal(expected, LibraryService.ClassifyExtension(extension));

    [Theory]
    [InlineData("https://www.zap.co.il/models/oled-tv", "zap.co.il/models/oled-tv")]
    [InlineData("https://github.com/", "github.com")]
    [InlineData("https://example.com/a%20b", "example.com/a b")]
    [InlineData("https://example.com/?q=1", "example.com?q=1")]
    public void DescribeUrl_KeepsUntitledSourcesDistinguishable(string url, string expected)
        => Assert.Equal(expected, LibraryService.DescribeUrl(new Uri(url)));

    [Fact]
    public void ExtractReferences_UsesReadableUrlWhenSourceHasNoTitle()
    {
        var chat = new Chat { Id = Guid.NewGuid(), Title = "Research" };
        var messages = new List<ChatMessage>
        {
            new()
            {
                Role = "assistant",
                Timestamp = Base,
                Sources =
                [
                    new SearchSource { Url = "https://www.zap.co.il/models/oled" },
                    new SearchSource { Url = "https://www.zap.co.il/models/qled" }
                ]
            }
        };

        var names = LibraryService.ExtractReferences(chat, messages).Select(r => r.Name).ToList();

        Assert.Equal(["zap.co.il/models/oled", "zap.co.il/models/qled"], names);
    }

    [Fact]
    public void ExtractReferences_ReplacesHostOnlyTitlesWithTheReadableUrl()
    {
        var chat = new Chat { Id = Guid.NewGuid(), Title = "Research" };
        var messages = new List<ChatMessage>
        {
            new()
            {
                Role = "assistant",
                Timestamp = Base,
                Sources =
                [
                    new SearchSource { Url = "https://www.zap.co.il/models/oled", Title = "www.zap.co.il" },
                    new SearchSource { Url = "https://www.zap.co.il/deals", Title = "zap.co.il" },
                    new SearchSource { Url = "https://www.zap.co.il/reviews", Title = "Best OLED deals" }
                ]
            }
        };

        var names = LibraryService.ExtractReferences(chat, messages).Select(r => r.Name).ToList();

        Assert.Equal(["zap.co.il/models/oled", "zap.co.il/deals", "Best OLED deals"], names);
    }

    [Fact]
    public void PathForKind_GivesEveryKindADistinctNonZeroFillGlyph()
    {
        var kinds = Enum.GetValues<LibraryArtifactKind>();
        var paths = kinds.Select(LibraryIcons.PathForKind).ToList();

        Assert.All(paths, path =>
        {
            Assert.StartsWith("F1 ", path);
            Assert.Contains("z", path);
        });
        // Only "Other" shares the generic file glyph, so every kind reads distinctly.
        Assert.Equal(kinds.Length, paths.Distinct().Count());
    }

    [Fact]
    public void BuildWorktreeReference_NamesTheWorktreeAfterItsFolderAndCreditsTheChat()
    {
        var projectId = Guid.NewGuid();
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Title = "Library feature",
            ProjectId = projectId,
            UpdatedAt = Base,
            WorktreePath = @"E:\Git\Lumi-wt-lumi-09a37e92\"
        };

        var reference = LibraryService.BuildWorktreeReference(chat);

        Assert.NotNull(reference);
        Assert.Equal("Lumi-wt-lumi-09a37e92", reference!.Name);
        Assert.Equal(@"E:\Git\Lumi-wt-lumi-09a37e92", reference.Location);
        Assert.Equal(LibraryArtifactKind.Worktree, reference.Kind);
        Assert.Equal(LibraryArtifactOrigin.Created, reference.Origin);
        Assert.Equal(chat.Id, reference.ChatId);
        Assert.Equal("Library feature", reference.ChatTitle);
        Assert.Equal(projectId, reference.ProjectId);
        Assert.Equal(Base, reference.Timestamp);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildWorktreeReference_SkipsChatsThatNeverCheckedOneOut(string? worktreePath)
        => Assert.Null(LibraryService.BuildWorktreeReference(
            new Chat { Id = Guid.NewGuid(), Title = "Plain chat", WorktreePath = worktreePath }));

    [Fact]
    public void Merge_ProbesWorktreesAsDirectoriesAndNeverSizesThem()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"lumi-worktree-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "README.md"), "not counted");
        try
        {
            var artifact = Assert.Single(LibraryService.Merge(
                [Worktree(directory, Guid.NewGuid(), "Chat", Base)],
                new Dictionary<Guid, string>()));

            Assert.True(artifact.Exists);
            // Sizing a worktree means walking a whole checkout, which the Library never does.
            Assert.Equal(0, artifact.SizeBytes);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Merge_MarksDeletedWorktreesAsGone()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"lumi-worktree-gone-{Guid.NewGuid():N}");

        var artifact = Assert.Single(LibraryService.Merge(
            [Worktree(directory, Guid.NewGuid(), "Chat", Base)],
            new Dictionary<Guid, string>()));

        Assert.False(artifact.Exists);
    }

    [Fact]
    public void Merge_ReusesCachedDiskMetadataAcrossProgressReports()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lumi-library-cache-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "first scan");
        var cache = new Dictionary<string, LibraryService.FileStat>(StringComparer.OrdinalIgnoreCase);
        var references = new[] { Reference(path, LibraryArtifactOrigin.Created, Guid.NewGuid(), "Chat", Base) };

        try
        {
            var first = Assert.Single(LibraryService.Merge(references, new Dictionary<Guid, string>(), cache));
            Assert.True(first.Exists);

            // A scan re-merges the whole corpus on every progress report; the cache is what stops it
            // from re-stating every file it has already seen.
            File.Delete(path);
            var second = Assert.Single(LibraryService.Merge(references, new Dictionary<Guid, string>(), cache));

            Assert.True(second.Exists);
            Assert.Equal(first.SizeBytes, second.SizeBytes);
            Assert.Single(cache);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void HasSameDisplayData_SeparatesUnchangedRebindsFromRealChanges()
    {
        var chatId = Guid.NewGuid();
        var references = new[] { Reference(@"C:\tmp\a.png", LibraryArtifactOrigin.Sent, chatId, "Chat", Base) };
        var projects = new Dictionary<Guid, string>();

        var first = Assert.Single(LibraryService.Merge(references, projects));
        var identical = Assert.Single(LibraryService.Merge(references, projects));
        var renamedChat = Assert.Single(LibraryService.Merge(
            [Reference(@"C:\tmp\a.png", LibraryArtifactOrigin.Sent, chatId, "Renamed chat", Base)],
            projects));

        Assert.False(ReferenceEquals(first, identical));
        Assert.True(first.HasSameDisplayData(identical));
        Assert.False(first.HasSameDisplayData(renamedChat));
    }

    private static LibraryService.ArtifactReference Reference(        string path,
        LibraryArtifactOrigin origin,
        Guid chatId,
        string chatTitle,
        DateTimeOffset timestamp,
        Guid? projectId = null)
        => new(
            Key: path,
            Location: path,
            Name: Path.GetFileName(path),
            Extension: Path.GetExtension(path),
            Kind: LibraryService.ClassifyExtension(Path.GetExtension(path)),
            Origin: origin,
            ChatId: chatId,
            ChatTitle: chatTitle,
            ProjectId: projectId,
            Timestamp: timestamp,
            Description: null);

    private static LibraryService.ArtifactReference Worktree(
        string path,
        Guid chatId,
        string chatTitle,
        DateTimeOffset timestamp)
        => LibraryService.BuildWorktreeReference(new Chat
        {
            Id = chatId,
            Title = chatTitle,
            UpdatedAt = timestamp,
            WorktreePath = path
        })!;
    [Theory]
    [InlineData(".png")]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".gif")]
    [InlineData(".bmp")]
    [InlineData(".webp")]
    [InlineData(".ico")]
    [InlineData(".tiff")]
    [InlineData(".tif")]
    public void ClassifyExtension_TreatsEveryDecodableImageAsAnImageKind(string extension)
    {
        // The Library routes previews off the extension the scan already classified, so anything the
        // icon helper would decode from disk must land on the Image kind. Otherwise it falls through
        // to the shell-icon branch, which runs on the UI thread and would stat and decode the file
        // there - a stall on every row that scrolls into view.
        Assert.True(FileIconHelper.IsImageExtension(extension));
        Assert.Equal(LibraryArtifactKind.Image, LibraryService.ClassifyExtension(extension));
    }
    [Fact]
    public async Task ScanAsync_WalksTheCallersSnapshotSoAChatAddedMidScanCannotFaultIt()
    {
        // AppData.Chats is a plain List<Chat> that the UI thread mutates - a background job finishing
        // a chat, for instance. A full scan runs for a minute on a background thread, so it has to walk
        // a snapshot the caller captured; enumerating the live list would throw "collection was
        // modified" partway through and lose the entire scan.
        var data = new AppData();
        data.Chats.Add(new Chat
        {
            Id = Guid.NewGuid(),
            Title = "Checked out a worktree",
            UpdatedAt = Base,
            WorktreePath = Path.Combine(Path.GetTempPath(), "lumi-snapshot-original")
        });

        var service = new LibraryService(new DataStore(data));
        var chats = data.Chats.ToArray();
        var projects = data.Projects.ToArray();

        data.Chats.Add(new Chat
        {
            Id = Guid.NewGuid(),
            Title = "Started while the scan was already running",
            UpdatedAt = Base.AddMinutes(1),
            WorktreePath = Path.Combine(Path.GetTempPath(), "lumi-snapshot-latecomer")
        });

        var artifacts = await service.ScanAsync(chats, projects, progress: null, CancellationToken.None);

        Assert.Equal(
            new[] { "lumi-snapshot-original" },
            artifacts.Select(artifact => artifact.Name).ToArray());
    }
    [Theory]
    [InlineData(".svg")]
    [InlineData(".heic")]
    [InlineData(".avif")]
    public void ClassifyExtension_MarksUndecodableFormatsAsImagesThatMustNotTakeTheDecodePath(string extension)
    {
        // These read as images to the user, so they earn the image kind and its accent. The decoder
        // cannot open them though, so the row must route them to the cached shell icon instead - and
        // show it as a small badge rather than stretching it to fill a cover tile.
        Assert.Equal(LibraryArtifactKind.Image, LibraryService.ClassifyExtension(extension));
        Assert.False(FileIconHelper.IsImageExtension(extension));
    }

    [Fact]
    public void Merge_OrdersArtifactsDeterministicallyWhenTimestampsTie()
    {
        // A scan re-merges the whole corpus on every progress report. List.Sort is unstable, so
        // without a tiebreaker artifacts sharing a timestamp (several attachments on one message)
        // reshuffle between reports and the rows visibly jump while the scan runs.
        var chatId = Guid.NewGuid();
        var shared = Base.AddHours(3);
        var references = new[]
        {
            Reference(Path.Combine(Path.GetTempPath(), "charlie.txt"), LibraryArtifactOrigin.Sent, chatId, "Chat", shared),
            Reference(Path.Combine(Path.GetTempPath(), "alpha.txt"), LibraryArtifactOrigin.Sent, chatId, "Chat", shared),
            Reference(Path.Combine(Path.GetTempPath(), "bravo.txt"), LibraryArtifactOrigin.Sent, chatId, "Chat", shared)
        };

        var first = LibraryService.Merge(references, new Dictionary<Guid, string>())
            .Select(artifact => artifact.Name)
            .ToArray();
        var reversed = LibraryService.Merge(references.Reverse().ToArray(), new Dictionary<Guid, string>())
            .Select(artifact => artifact.Name)
            .ToArray();

        Assert.Equal(new[] { "alpha.txt", "bravo.txt", "charlie.txt" }, first);
        Assert.Equal(first, reversed);
    }
}
