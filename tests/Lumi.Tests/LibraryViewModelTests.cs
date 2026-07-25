using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Lumi.Models;
using Lumi.Localization;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public class LibraryViewModelTests
{
    private static string Path(string name) => System.IO.Path.Combine(System.IO.Path.GetTempPath(), name);

    private static LibraryViewModel BuildViewModel(params Chat[] chats)
        => new(new DataStore(new AppData { Chats = [.. chats] }));

    private static Chat ChatWith(string title, params ChatMessage[] messages)
        => new()
        {
            Id = Guid.NewGuid(),
            Title = title,
            UpdatedAt = DateTimeOffset.UtcNow,
            Messages = [.. messages]
        };

    private static ChatMessage Sent(string path, DateTimeOffset when)
        => new() { Role = "user", Timestamp = when, Attachments = [path] };

    private static ChatMessage Created(string path, DateTimeOffset when)
        => new()
        {
            Role = "tool",
            ToolName = "announce_file",
            Timestamp = when,
            Content = $"{{\"filePath\":\"{path.Replace("\\", "\\\\")}\"}}"
        };

    private static ChatMessage Cited(string url, DateTimeOffset when)
        => new() { Role = "assistant", Timestamp = when, Sources = [new SearchSource { Url = url, Title = "Source" }] };

    [Fact]
    public async Task Refresh_AggregatesArtifactsFromEveryChat()
    {
        var now = DateTimeOffset.UtcNow;
        var vm = BuildViewModel(
            ChatWith("Design", Sent(Path("mock.png"), now), Cited("https://example.com/a", now)),
            ChatWith("Report", Created(Path("summary.docx"), now.AddMinutes(-5))));

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.True(vm.HasScanned);
        Assert.True(vm.HasAnyArtifacts);
        Assert.Equal(2, vm.TotalFiles);
        Assert.Equal(1, vm.TotalLinks);
        Assert.Equal(2, vm.SourceChatCount);
        Assert.Equal(3, vm.Items.Count);
    }

    [Fact]
    public async Task SelectingCollectionFilter_NarrowsResultsAndClearFiltersRestoresThem()
    {
        var now = DateTimeOffset.UtcNow;
        var vm = BuildViewModel(ChatWith(
            "Mixed",
            Sent(Path("photo.png"), now),
            Created(Path("notes.md"), now.AddMinutes(-1))));

        await vm.RefreshCommand.ExecuteAsync(null);

        var images = vm.Kinds.Single(option => option.Id == nameof(LibraryArtifactKind.Image));
        Assert.Equal(1, images.Count);

        images.SelectCommand.Execute(null);

        Assert.True(vm.HasActiveFilters);
        Assert.Single(vm.Items);
        Assert.Equal("photo.png", vm.Items[0].Name);

        vm.ClearFiltersCommand.Execute(null);

        Assert.False(vm.HasActiveFilters);
        Assert.Equal(2, vm.Items.Count);
    }

    [Fact]
    public async Task OriginFilter_SeparatesSentFromCreatedArtifacts()
    {
        var now = DateTimeOffset.UtcNow;
        var vm = BuildViewModel(ChatWith(
            "Mixed",
            Sent(Path("input.csv"), now),
            Created(Path("output.csv"), now)));

        await vm.RefreshCommand.ExecuteAsync(null);

        vm.Origins.Single(option => option.Id == nameof(LibraryArtifactOrigin.Created)).SelectCommand.Execute(null);

        var item = Assert.Single(vm.Items);
        Assert.Equal("output.csv", item.Name);
    }

    [Fact]
    public async Task Search_MatchesArtifactNamesAndClearsBackToFullList()
    {
        var now = DateTimeOffset.UtcNow;
        var vm = BuildViewModel(ChatWith(
            "Mixed",
            Sent(Path("quarterly-budget.xlsx"), now),
            Sent(Path("holiday-photo.png"), now)));

        await vm.RefreshCommand.ExecuteAsync(null);

        vm.SearchQuery = "budget";

        var item = Assert.Single(vm.Items);
        Assert.Equal("quarterly-budget.xlsx", item.Name);

        vm.ClearSearchCommand.Execute(null);
        Assert.Equal(2, vm.Items.Count);
    }

    [Fact]
    public async Task DetailLabels_SwitchBetweenFileAndLinkWording()
    {
        var now = DateTimeOffset.UtcNow;
        var vm = BuildViewModel(ChatWith(
            "Mixed",
            Sent(Path("quarterly-budget.xlsx"), now),
            Cited("https://zap.co.il/models/oled", now)));

        await vm.RefreshCommand.ExecuteAsync(null);

        var file = vm.Items.Single(i => !i.IsLink);
        var link = vm.Items.Single(i => i.IsLink);

        Assert.Equal(Loc.Library_Field_Size, file.SizeFieldLabel);
        Assert.Equal(Loc.Library_Action_CopyPath, file.CopyActionLabel);
        Assert.Equal(Loc.Library_Field_Site, link.SizeFieldLabel);
        Assert.Equal(Loc.Library_Action_CopyLink, link.CopyActionLabel);
        Assert.Equal("zap.co.il", link.SizeLabel);
    }

    [Fact]
    public async Task ProgressiveScan_ReusesItemViewModelsSoPreviewsAndSelectionSurvive()
    {
        var now = DateTimeOffset.UtcNow;
        var vm = BuildViewModel(ChatWith("Docs", Sent(Path("plan.docx"), now)));

        await vm.RefreshCommand.ExecuteAsync(null);
        var first = vm.Items.Single();
        vm.SelectedItem = first;

        // A second scan republishes the same artifact; the card must be the very same instance so
        // the decoded preview and the open detail pane are not thrown away mid-scan.
        await vm.RefreshCommand.ExecuteAsync(null);
        var second = vm.Items.Single();

        Assert.Same(first, second);
        Assert.Same(first, vm.SelectedItem);
        Assert.True(first.IsSelected);
    }

    [Fact]
    public async Task ScanProgress_ReportsChatCountsAndCompletes()
    {
        var now = DateTimeOffset.UtcNow;
        var vm = BuildViewModel(
            ChatWith("One", Sent(Path("a.pdf"), now)),
            ChatWith("Two", Sent(Path("b.pdf"), now)));

        Assert.Equal(0, vm.ScanFraction);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.TotalChatsToScan);
        Assert.Equal(2, vm.ScannedChats);
        Assert.Equal(1, vm.ScanFraction);
        Assert.Contains("2", vm.ScanProgressLabel);
    }

    [Fact]
    public void CollectionFacets_CarryVectorIconPathsRatherThanEmoji()
    {
        var vm = BuildViewModel();

        Assert.All(vm.Kinds, option =>
        {
            Assert.True(option.HasIcon);
            Assert.StartsWith("F1 ", option.IconPath);
        });
    }

    [Fact]
    public async Task NoMatchingResults_ReportsEmptyResultRatherThanEmptyLibrary()
    {
        var vm = BuildViewModel(ChatWith("Mixed", Sent(Path("photo.png"), DateTimeOffset.UtcNow)));

        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SearchQuery = "zzz-nothing-matches-zzz";

        Assert.True(vm.IsEmptyResult);
        Assert.False(vm.IsEmptyLibrary);
    }

    [Fact]
    public async Task EmptyHistory_ReportsEmptyLibrary()
    {
        var vm = BuildViewModel();

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.True(vm.IsEmptyLibrary);
        Assert.False(vm.HasAnyArtifacts);
        Assert.False(vm.HasProjectFilters);
    }

    [Fact]
    public async Task SelectingItem_ExclusivelyHighlightsIt()
    {
        var now = DateTimeOffset.UtcNow;
        var vm = BuildViewModel(ChatWith(
            "Mixed",
            Sent(Path("a.png"), now),
            Sent(Path("b.png"), now.AddMinutes(-1))));

        await vm.RefreshCommand.ExecuteAsync(null);

        var items = vm.Items.ToList();
        items[0].SelectCommand.Execute(null);
        Assert.Same(items[0], vm.SelectedItem);

        items[1].SelectCommand.Execute(null);
        Assert.Same(items[1], vm.SelectedItem);
        Assert.False(items[0].IsSelected);
        Assert.True(items[1].IsSelected);
    }

    [Fact]
    public async Task SortByName_FlattensGroupsIntoAlphabeticalOrder()
    {
        var now = DateTimeOffset.UtcNow;
        var vm = BuildViewModel(ChatWith(
            "Mixed",
            Sent(Path("zebra.txt"), now),
            Sent(Path("apple.txt"), now.AddMinutes(-1))));

        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedSortIndex = 2; // Name

        var names = vm.Items.Select(item => item.Name).ToList();
        Assert.Equal(["apple.txt", "zebra.txt"], names);
    }

    [Fact]
    public async Task ProjectFilters_AppearOnlyWhenArtifactsSpanProjects()
    {
        var project = new Project { Id = Guid.NewGuid(), Name = "Lumi" };
        var now = DateTimeOffset.UtcNow;

        var projectChat = ChatWith("In project", Sent(Path("in-project.txt"), now));
        projectChat.ProjectId = project.Id;
        var looseChat = ChatWith("Loose", Sent(Path("loose.txt"), now));

        var store = new DataStore(new AppData { Chats = [projectChat, looseChat], Projects = [project] });
        var vm = new LibraryViewModel(store);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.True(vm.HasProjectFilters);
        var projectFilter = vm.Projects.Single(option => option.Label == "Lumi");
        Assert.Equal(1, projectFilter.Count);

        projectFilter.SelectCommand.Execute(null);
        var item = Assert.Single(vm.Items);
        Assert.Equal("in-project.txt", item.Name);
        Assert.Equal("Lumi", item.ProjectName);
    }

    [Fact]
    public async Task SharedArtifact_ReportsEveryChatThatReferencesIt()
    {
        var now = DateTimeOffset.UtcNow;
        var shared = Path("shared-brief.pdf");
        var vm = BuildViewModel(
            ChatWith("First", Sent(shared, now.AddDays(-2))),
            ChatWith("Second", Sent(shared, now)));

        await vm.RefreshCommand.ExecuteAsync(null);

        var item = Assert.Single(vm.Items);
        Assert.True(item.IsSharedAcrossChats);
        Assert.Equal("Second", item.ChatTitle);
    }

  
    [Fact]
    public async Task LargeLibrary_BindsEveryMatchBecauseTheListVirtualizes()
    {
        var now = DateTimeOffset.UtcNow;
        var messages = Enumerable.Range(0, 260)
            .Select(index => Sent(Path($"paged-{index:D3}.txt"), now.AddMinutes(-index)))
            .ToArray();

        var vm = BuildViewModel(ChatWith("Bulk", messages));

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(260, vm.Items.Count);
    }

    [Fact]
    public async Task DateSort_StampsAGroupHeaderOnlyOnTheFirstRowOfEachBucket()
    {
        var now = DateTimeOffset.Now;
        var vm = BuildViewModel(ChatWith(
            "Mixed",
            Sent(Path("today-a.txt"), now.AddMinutes(-1)),
            Sent(Path("today-b.txt"), now.AddMinutes(-2)),
            Sent(Path("old.txt"), now.AddDays(-30))));

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.True(vm.Items[0].HasGroupHeader);
        Assert.False(vm.Items[1].HasGroupHeader);
        Assert.True(vm.Items[2].HasGroupHeader);
        Assert.NotEqual(vm.Items[0].GroupHeader, vm.Items[2].GroupHeader);
    }

    [Fact]
    public async Task SearchingOrSortingByName_DropsTheDateSeparators()
    {
        var now = DateTimeOffset.Now;
        var vm = BuildViewModel(ChatWith(
            "Mixed",
            Sent(Path("alpha.txt"), now.AddMinutes(-1)),
            Sent(Path("beta.txt"), now.AddDays(-30))));

        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Contains(vm.Items, item => item.HasGroupHeader);

        vm.SelectedSortIndex = 2; // Name
        Assert.All(vm.Items, item => Assert.False(item.HasGroupHeader));

        vm.SelectedSortIndex = 0;
        vm.SearchQuery = "a";
        Assert.All(vm.Items, item => Assert.False(item.HasGroupHeader));
    }

    [Fact]
    public async Task MetaLine_CombinesOriginAndSourceChat()
    {
        var now = DateTimeOffset.UtcNow;
        var vm = BuildViewModel(ChatWith("Design review", Sent(Path("brief.pdf"), now)));

        await vm.RefreshCommand.ExecuteAsync(null);

        var item = Assert.Single(vm.Items);
        Assert.Contains(item.OriginLabel, item.MetaLine);
        Assert.Contains("Design review", item.MetaLine);
    }

    [Fact]
    public async Task SelectingAnItem_RevealsTheDetailPane()
    {
        var now = DateTimeOffset.UtcNow;
        var vm = BuildViewModel(ChatWith("Mixed", Sent(Path("a.png"), now)));

        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(vm.HasSelection);

        vm.Items[0].SelectCommand.Execute(null);
        Assert.True(vm.HasSelection);
    }

    [Fact]
    public async Task Highlights_ShowOnlyFilesAndHideWhileFiltering()
    {
        var now = DateTimeOffset.UtcNow;
        var vm = BuildViewModel(ChatWith(
            "Mixed",
            Sent(Path("hero.png"), now),
            Created(Path("notes.md"), now.AddMinutes(-1)),
            Cited("https://example.com/a", now.AddMinutes(-2))));

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.True(vm.HasHighlights);
        Assert.Equal(2, vm.Highlights.Count);
        Assert.All(vm.Highlights, item => Assert.NotEqual(LibraryArtifactKind.Link, item.Artifact.Kind));

        vm.Kinds.Single(option => option.Id == nameof(LibraryArtifactKind.Image)).SelectCommand.Execute(null);

        Assert.False(vm.HasHighlights);
        Assert.Empty(vm.Highlights);

        vm.ClearFiltersCommand.Execute(null);

        Assert.True(vm.HasHighlights);
        Assert.Equal(2, vm.Highlights.Count);
    }

    [Fact]
    public async Task FilterOptions_CarryCollectionAccentForKindFacets()
    {
        var vm = BuildViewModel(ChatWith("Mixed", Sent(Path("hero.png"), DateTimeOffset.UtcNow)));

        await vm.RefreshCommand.ExecuteAsync(null);

        var images = vm.Kinds.Single(option => option.Id == nameof(LibraryArtifactKind.Image));
        var everything = vm.Kinds.Single(option => option.Id == "all");

        Assert.Equal(LibraryArtifactKind.Image, images.Kind);
        Assert.Same(LibraryPalette.Accent(LibraryArtifactKind.Image), images.Accent);
        Assert.Null(everything.Kind);
        Assert.Same(LibraryPalette.NeutralAccent, everything.Accent);
    }
    [Fact]
    public async Task EmptyResultState_OnlyAppearsWhileAFilterIsActuallyActive()
    {
        var vm = BuildViewModel(ChatWith("Mixed", Sent(Path("hero.png"), DateTimeOffset.UtcNow)));

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.False(vm.HasActiveFilters);
        Assert.False(vm.IsEmptyResult);

        vm.SearchQuery = "zzzz-no-such-artifact";

        Assert.True(vm.HasActiveFilters);
        Assert.False(vm.HasResults);
        Assert.True(vm.IsEmptyResult);

        vm.ClearFiltersCommand.Execute(null);

        Assert.False(vm.HasActiveFilters);
        Assert.True(vm.HasResults);
        Assert.False(vm.IsEmptyResult);
    }

    [Fact]
    public async Task EmptyResultState_StaysHiddenWhenAnUnfilteredListIsMomentarilyEmpty()
    {
        var vm = BuildViewModel();

        await vm.RefreshCommand.ExecuteAsync(null);

        // No artifacts at all is the empty-library state, never the "clear filters" card.
        Assert.False(vm.IsEmptyResult);
        Assert.True(vm.IsEmptyLibrary);
    }

    [Fact]
    public async Task ActiveFilters_NameTheFacetEachConstraintCameFrom()
    {
        var vm = BuildViewModel(ChatWith(
            "Mixed",
            Sent(Path("hero.png"), DateTimeOffset.UtcNow),
            Created(Path("report.pdf"), DateTimeOffset.UtcNow)));

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Empty(vm.ActiveFilters);
        Assert.False(vm.HasMultipleActiveFilters);

        vm.Kinds.First(option => option.Id == nameof(LibraryArtifactKind.Image)).SelectCommand.Execute(null);
        vm.SearchQuery = "hero";

        Assert.Equal(2, vm.ActiveFilters.Count);
        Assert.True(vm.HasMultipleActiveFilters);
        Assert.Contains(vm.ActiveFilters, chip => chip.Category == Loc.Library_Chip_Search && chip.Label.Contains("hero"));
        Assert.Contains(vm.ActiveFilters, chip => chip.Category == Loc.Library_Chip_Collection);
    }

    [Fact]
    public async Task ActiveFilterChip_RemovesOnlyItsOwnConstraint()
    {
        var vm = BuildViewModel(ChatWith(
            "Mixed",
            Sent(Path("hero.png"), DateTimeOffset.UtcNow),
            Created(Path("hero-notes.md"), DateTimeOffset.UtcNow)));

        await vm.RefreshCommand.ExecuteAsync(null);

        vm.Kinds.First(option => option.Id == nameof(LibraryArtifactKind.Image)).SelectCommand.Execute(null);
        vm.SearchQuery = "hero";

        vm.ActiveFilters.First(chip => chip.Category == Loc.Library_Chip_Collection).Remove();

        // The collection constraint is gone; the typed query is untouched.
        Assert.Equal("hero", vm.SearchQuery);
        Assert.Single(vm.ActiveFilters);
        Assert.Equal(Loc.Library_Chip_Search, vm.ActiveFilters[0].Category);
        Assert.True(vm.Kinds.First().IsSelected);
    }

    [Fact]
    public async Task ClearSearch_LeavesRailSelectionsInPlace()
    {
        var vm = BuildViewModel(ChatWith(
            "Mixed",
            Sent(Path("hero.png"), DateTimeOffset.UtcNow),
            Created(Path("report.pdf"), DateTimeOffset.UtcNow)));

        await vm.RefreshCommand.ExecuteAsync(null);

        vm.Kinds.First(option => option.Id == nameof(LibraryArtifactKind.Image)).SelectCommand.Execute(null);
        vm.SearchQuery = "hero";
        Assert.True(vm.HasSearchQuery);

        vm.ClearSearchCommand.Execute(null);

        Assert.False(vm.HasSearchQuery);
        Assert.True(vm.HasActiveFilters);
        Assert.Single(vm.ActiveFilters);
        Assert.Equal(Loc.Library_Chip_Collection, vm.ActiveFilters[0].Category);
    }

    [Fact]
    public async Task TimeWindow_FiltersThroughTheSameFacetTheChipRemoves()
    {
        var vm = BuildViewModel(ChatWith(
            "Mixed",
            Sent(Path("today.png"), DateTimeOffset.Now),
            Sent(Path("ancient.png"), DateTimeOffset.Now.AddDays(-120))));

        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Items.Count);

        vm.SelectedTimeRangeOption = vm.TimeRanges.First(option => option.Id == nameof(LibraryTimeRange.Today));

        Assert.Single(vm.Items);
        Assert.Single(vm.ActiveFilters);
        Assert.Equal(Loc.Library_Chip_When, vm.ActiveFilters[0].Category);

        vm.ActiveFilters[0].Remove();

        // Removing the chip has to snap the bound dropdown back to "any time", not just the facet.
        Assert.Equal(2, vm.Items.Count);
        Assert.Empty(vm.ActiveFilters);
        Assert.Equal("all", vm.SelectedTimeRangeOption?.Id);
    }
    [Fact]
    public async Task Worktrees_AppearAsTheirOwnCollectionCreditedToTheChatThatMadeThem()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"lumi-wt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var chat = ChatWith("Feature work", Sent(Path("spec.md"), DateTimeOffset.Now));
            chat.WorktreePath = directory;
            var vm = BuildViewModel(chat);

            await vm.RefreshCommand.ExecuteAsync(null);

            // A worktree is a directory, so it is counted apart from the files.
            Assert.Equal(1, vm.TotalFiles);
            Assert.Equal(1, vm.TotalWorktrees);
            Assert.Equal(2, vm.Items.Count);

            var worktrees = vm.Kinds.Single(option => option.Id == nameof(LibraryArtifactKind.Worktree));
            Assert.Equal(1, worktrees.Count);
            worktrees.SelectCommand.Execute(null);

            var row = Assert.Single(vm.Items);
            Assert.Equal(System.IO.Path.GetFileName(directory), row.Name);
            Assert.Equal("Feature work", row.ChatTitle);
            Assert.Equal(Loc.Library_Kind_Worktree, row.KindLabel);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task WorktreeRow_ReportsWhetherItIsStillOnDiskInsteadOfAByteSize()
    {
        var present = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"lumi-wt-live-{Guid.NewGuid():N}");
        var gone = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"lumi-wt-gone-{Guid.NewGuid():N}");
        Directory.CreateDirectory(present);
        try
        {
            var live = ChatWith("Live");
            live.WorktreePath = present;
            var stale = ChatWith("Stale");
            stale.WorktreePath = gone;

            var vm = BuildViewModel(live, stale);
            await vm.RefreshCommand.ExecuteAsync(null);

            var liveRow = vm.Items.Single(item => item.ChatTitle == "Live");
            var staleRow = vm.Items.Single(item => item.ChatTitle == "Stale");

            Assert.Equal(Loc.Library_Worktree_Present, liveRow.SizeLabel);
            Assert.Equal(Loc.Library_Missing, staleRow.SizeLabel);
            Assert.Equal(Loc.Library_Field_Status, liveRow.SizeFieldLabel);
            Assert.Equal(Loc.Library_Action_OpenFolder, liveRow.OpenActionLabel);
            Assert.True(staleRow.IsMissing);
        }
        finally
        {
            Directory.Delete(present, recursive: true);
        }
    }

    [Fact]
    public async Task Highlights_SkipWorktreesAndLinksSoTheGalleryShowsRealFiles()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"lumi-wt-band-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var chat = ChatWith(
                "Mixed",
                Sent(Path("shot.png"), DateTimeOffset.Now),
                Cited("https://example.com/a", DateTimeOffset.Now));
            chat.WorktreePath = directory;

            var vm = BuildViewModel(chat);
            await vm.RefreshCommand.ExecuteAsync(null);

            Assert.All(vm.Highlights, item => Assert.False(item.IsLink));
            Assert.DoesNotContain(vm.Highlights, item => item.KindLabel == Loc.Library_Kind_Worktree);
            Assert.Contains(vm.Highlights, item => item.Name == "shot.png");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
    [Fact]
    public async Task Refresh_ClickedDuringTheInitialScanDoesNotFaultTheSupersededScan()
    {
        // Navigating to the Library starts the scan through EnsureLoadedAsync, which bypasses
        // RefreshCommand - so the command's own concurrency guard is never engaged and the Rescan
        // button stays live. This covers that overlap: the first scan is superseded while it is
        // suspended on I/O, and both calls still have to settle with correct final state.
        //
        // The chats carry a worktree but no in-memory messages, so every one takes the async
        // persisted-message read, which is what keeps the first scan suspended long enough to be
        // superseded. Note this exercises the cancellation unwind, not the narrower race where a
        // Progress callback is already queued on the dispatcher when the token source is disposed;
        // that one needs a queuing SynchronizationContext to hit and is held by RefreshAsync
        // capturing its token up front rather than re-reading the (possibly disposed) source.
        var chats = Enumerable.Range(0, 400)
            .Select(index => new Chat
            {
                Id = Guid.NewGuid(),
                Title = $"Chat {index}",
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-index),
                WorktreePath = Path($"lumi-superseded-{index}")
            })
            .ToArray();
        var vm = BuildViewModel(chats);

        var navigationScan = vm.EnsureLoadedAsync();
        var userRescan = vm.RefreshCommand.ExecuteAsync(null);

        await Task.WhenAll(navigationScan, userRescan);

        Assert.True(vm.HasScanned);
        Assert.False(vm.IsLoading);
        Assert.Equal(400, vm.Items.Count);
    }
}
