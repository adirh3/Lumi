using System;
using System.Collections.Generic;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public class SearchOverlayViewModelTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("a", false)]
    [InlineData("ab", true)]
    [InlineData("  history  ", true)]
    public void ShouldRunFullSearch_RequiresMeaningfulQuery(string? query, bool expected)
    {
        Assert.Equal(expected, SearchOverlayViewModel.ShouldRunFullSearch(query));
    }

    [Fact]
    public void ResolveSelectedIndex_PreservesSameResultAcrossRefinement()
    {
        var item = new object();
        var previous = Result("Target", item);
        var refined = new List<SearchResultItem>
        {
            Result("New"),
            Result("Target", item),
            Result("Other")
        };

        var selectedIndex = SearchOverlayViewModel.ResolveSelectedIndex(previous, refined, 0);

        Assert.Equal(1, selectedIndex);
    }

    [Fact]
    public void ResolveSelectedIndex_ClampsWhenSelectedResultDisappears()
    {
        var selectedIndex = SearchOverlayViewModel.ResolveSelectedIndex(
            Result("Missing"),
            [Result("One"), Result("Two")],
            8);

        Assert.Equal(1, selectedIndex);
    }

    [Fact]
    public void ResolveSelectedIndex_StartsAtFirstResultForNewQuery()
    {
        var selectedIndex = SearchOverlayViewModel.ResolveSelectedIndex(
            previousSelectedItem: null,
            [Result("One"), Result("Two")],
            currentSelectedIndex: -1);

        Assert.Equal(0, selectedIndex);
    }

    [Fact]
    public void MoveSelection_WrapsInBothDirections()
    {
        var vm = CreateViewModel();
        vm.FlatResults = [Result("One"), Result("Two"), Result("Three")];

        vm.SelectedIndex = 2;
        vm.MoveSelection(1);
        Assert.Equal(0, vm.SelectedIndex);

        vm.MoveSelection(-1);
        Assert.Equal(2, vm.SelectedIndex);
    }

    [Fact]
    public void Close_ClearsResultsAndSearchActivity()
    {
        var vm = CreateViewModel();
        vm.FlatResults = [Result("One")];
        vm.IsSearching = true;
        vm.IsDeepSearching = true;
        vm.SelectedIndex = 0;

        vm.Close();

        Assert.False(vm.IsOpen);
        Assert.False(vm.IsSearchPending);
        Assert.Empty(vm.FlatResults);
        Assert.Empty(vm.ResultGroups);
        Assert.Equal(0, vm.ResultCount);
        Assert.Equal(0, vm.TotalResultCount);
        Assert.Equal(-1, vm.SelectedIndex);
    }

    [Fact]
    public void SearchStatusText_ExplainsProgressAndVisibleResultLimit()
    {
        var vm = CreateViewModel();
        vm.IsOpen = true;
        vm.SearchQuery = "needle";
        vm.IsSearching = true;

        Assert.Equal("Searching...", vm.SearchStatusText);

        vm.FlatResults = [Result("One")];
        vm.TotalResultCount = 5;
        Assert.Equal("1 of 5 results · refining...", vm.SearchStatusText);

        vm.IsSearching = false;
        vm.IsDeepSearching = true;
        Assert.Equal("1 of 5 results · checking older chats...", vm.SearchStatusText);

        vm.IsDeepSearching = false;
        Assert.Equal("1 of 5 results", vm.SearchStatusText);
    }

    [Fact]
    public void CategoryPriority_KeepsChatsAboveSkillsAndOtherSections()
    {
        var categories = Enum.GetValues<GlobalSearchCategory>()
            .OrderBy(SearchOverlayViewModel.GetCategoryPriority)
            .ToArray();

        Assert.Equal(
        [
            GlobalSearchCategory.Chats,
            GlobalSearchCategory.BackgroundJobs,
            GlobalSearchCategory.Projects,
            GlobalSearchCategory.Skills,
            GlobalSearchCategory.Lumis,
            GlobalSearchCategory.Memories,
            GlobalSearchCategory.McpServers,
            GlobalSearchCategory.Settings
        ], categories);
    }

    [Fact]
    public void AllCategoryView_ShowsBalancedPreviewWithChatsFirst()
    {
        var groups = SearchOverlayViewModel.BuildVisibleGroupsForCategory(
        [
            Group(GlobalSearchCategory.Chats, 12),
            Group(GlobalSearchCategory.Skills, 7),
            Group(GlobalSearchCategory.Memories, 3)
        ], selectedCategory: null);

        Assert.Collection(
            groups,
            group =>
            {
                Assert.Equal(GlobalSearchCategory.Chats, group.CategoryKey);
                Assert.Equal(6, group.Items.Count);
                Assert.Equal(12, group.TotalCount);
                Assert.True(group.ShowHeader);
                Assert.True(group.HasMore);
                Assert.Equal("View all 12", group.ViewAllText);
            },
            group =>
            {
                Assert.Equal(GlobalSearchCategory.Skills, group.CategoryKey);
                Assert.Equal(3, group.Items.Count);
                Assert.True(group.HasMore);
            },
            group =>
            {
                Assert.Equal(GlobalSearchCategory.Memories, group.CategoryKey);
                Assert.Equal(3, group.Items.Count);
                Assert.False(group.HasMore);
            });
    }

    [Fact]
    public void ResultsHeading_TracksActiveSearchScope()
    {
        var vm = CreateViewModel();

        Assert.Equal("Recent", vm.ResultsHeading);

        vm.SearchQuery = "memory";
        Assert.Equal("Best matches", vm.ResultsHeading);

        vm.SelectCategory(GlobalSearchCategory.Chats);
        Assert.Equal("Chats", vm.ResultsHeading);
    }

    [Fact]
    public void SelectedCategoryView_ShowsOnlyThatCategoryAndExpandsItsResults()
    {
        var groups = SearchOverlayViewModel.BuildVisibleGroupsForCategory(
        [
            Group(GlobalSearchCategory.Chats, 12),
            Group(GlobalSearchCategory.Skills, 7)
        ], GlobalSearchCategory.Chats);

        var group = Assert.Single(groups);
        Assert.Equal(GlobalSearchCategory.Chats, group.CategoryKey);
        Assert.Equal(12, group.Items.Count);
        Assert.False(group.ShowHeader);
        Assert.False(group.HasMore);
    }

    [Fact]
    public void AreEquivalent_DetectsVisibleResultChanges()
    {
        var item = new object();

        Assert.True(SearchOverlayViewModel.AreEquivalent(
            Result("Title", item, subtitle: "Snippet"),
            Result("Title", item, subtitle: "Snippet")));

        Assert.False(SearchOverlayViewModel.AreEquivalent(
            Result("Title", item, subtitle: "Old snippet"),
            Result("Title", item, subtitle: "New snippet")));
    }

    private static SearchOverlayViewModel CreateViewModel()
    {
        var service = new GlobalSearchService(
            () => new AppData(),
            _ => new ChatSearchSnapshot { Version = "empty" });
        return new SearchOverlayViewModel(service, () => 0);
    }

    private static SearchResultItem Result(string title, object? item = null, string subtitle = "")
        => new()
        {
            CategoryKey = GlobalSearchCategory.Chats,
            Category = "Chats",
            Title = title,
            Subtitle = subtitle,
            Item = item
        };

    private static SearchResultGroup Group(GlobalSearchCategory category, int count)
        => new()
        {
            CategoryKey = category,
            Category = category.ToString(),
            TotalCount = count,
            Items = Enumerable.Range(1, count)
                .Select(index => Result($"{category} {index}"))
                .ToList()
        };
}
