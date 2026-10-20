using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Capabilities;
using Lumi.Services.Discover;
using Lumi.ViewModels;
using Lumi.Views;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class AppNavigationHistoryTests
{
    [Fact]
    public Task BackAndForward_FollowEveryPlaceInTheNavigationBar() => Run(async (vm, _) =>
    {
        int[] pages = [0, MainViewModel.LibraryNavIndex, 1, 4, 7];
        foreach (var page in pages.Skip(1))
        {
            vm.SetNavCommand.Execute(page.ToString());
            await Settle();
        }

        foreach (var page in pages.Reverse().Skip(1))
        {
            Assert.True(await vm.TryNavigateHistoryAsync(-1));
            await Settle();
            Assert.Equal(page, vm.SelectedNavIndex);
        }
        Assert.False(await vm.TryNavigateHistoryAsync(-1));

        foreach (var page in pages.Skip(1))
        {
            Assert.True(await vm.TryNavigateHistoryAsync(1));
            await Settle();
            Assert.Equal(page, vm.SelectedNavIndex);
        }
        Assert.False(await vm.TryNavigateHistoryAsync(1));
    });

    [Fact]
    public Task SettingsAndMemories_RestoreSubpagesCategoriesAndDetails() => Run(async (vm, data) =>
    {
        vm.OpenSettingsCommand.Execute(null);
        await Settle();
        vm.SettingsVM.SelectedPageIndex = 3;
        await Settle();
        vm.SettingsVM.SelectedPageIndex = SettingsViewModel.MemoriesPageIndex;
        await Settle();
        vm.MemoriesVM.CategoryFilter = "Work";
        await Settle();
        vm.MemoriesVM.EditMemoryCommand.Execute(data.Memories[0]);
        await Settle();

        Assert.True(await vm.TryNavigateHistoryAsync(-1));
        Assert.Equal(MainViewModel.MemoriesNavIndex, vm.SelectedNavIndex);
        Assert.False(vm.MemoriesVM.IsEditing);
        Assert.Equal("Work", vm.MemoriesVM.CategoryFilter);

        Assert.True(await vm.TryNavigateHistoryAsync(-1));
        Assert.Null(vm.MemoriesVM.CategoryFilter);

        Assert.True(await vm.TryNavigateHistoryAsync(-1));
        Assert.Equal(7, vm.SelectedNavIndex);
        Assert.Equal(3, vm.SettingsVM.SelectedPageIndex);
        Assert.True(await vm.TryNavigateHistoryAsync(-1));
        Assert.Equal(0, vm.SettingsVM.SelectedPageIndex);

        Assert.True(await vm.TryNavigateHistoryAsync(1));
        Assert.Equal(3, vm.SettingsVM.SelectedPageIndex);
        Assert.True(await vm.TryNavigateHistoryAsync(1));
        Assert.Equal(MainViewModel.MemoriesNavIndex, vm.SelectedNavIndex);
        Assert.Equal(SettingsViewModel.MemoriesPageIndex, vm.SettingsVM.SelectedPageIndex);
        Assert.True(await vm.TryNavigateHistoryAsync(1));
        Assert.Equal("Work", vm.MemoriesVM.CategoryFilter);
        Assert.True(await vm.TryNavigateHistoryAsync(1));
        Assert.Same(data.Memories[0], vm.MemoriesVM.SelectedMemory);
        Assert.True(vm.MemoriesVM.IsEditing);
    });

    [Fact]
    public Task SettingsSearch_RestoresTheQueryWithoutAHistoryEntryForEveryKeystroke() => Run(async (vm, _) =>
    {
        vm.OpenSettingsCommand.Execute(null);
        await Settle();
        vm.SettingsVM.SelectedPageIndex = 3;
        await Settle();
        vm.SettingsVM.SearchQuery = "app";
        await Settle();
        vm.SettingsVM.SearchQuery = "appearance";
        await Settle();

        Assert.True(await vm.TryNavigateHistoryAsync(-1));
        Assert.Equal(3, vm.SettingsVM.SelectedPageIndex);
        Assert.False(vm.SettingsVM.IsSearching);
        Assert.True(await vm.TryNavigateHistoryAsync(-1));
        Assert.Equal(0, vm.SettingsVM.SelectedPageIndex);
        Assert.True(await vm.TryNavigateHistoryAsync(1));
        Assert.True(await vm.TryNavigateHistoryAsync(1));
        Assert.Equal("appearance", vm.SettingsVM.SearchQuery);

        vm.SettingsVM.SelectedPageIndex = SettingsViewModel.MemoriesPageIndex;
        await Settle();
        vm.SettingsVM.SearchQuery = "permissions";
        await Settle();
        Assert.Equal(7, vm.SelectedNavIndex);
        Assert.True(await vm.TryNavigateHistoryAsync(-1));
        Assert.Equal(MainViewModel.MemoriesNavIndex, vm.SelectedNavIndex);
        Assert.False(vm.SettingsVM.IsSearching);
        Assert.True(await vm.TryNavigateHistoryAsync(1));
        Assert.Equal(7, vm.SelectedNavIndex);
        Assert.Equal("permissions", vm.SettingsVM.SearchQuery);
    });

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public Task ManagementPages_RestoreOverviewExistingItemsAndNewEditors(int page) => Run(async (vm, data) =>
    {
        vm.SelectedNavIndex = page;
        await Settle();
        switch (page)
        {
            case 2: vm.ProjectsVM.OpenById(data.Projects[0].Id); break;
            case 3: vm.SkillsVM.OpenById(data.Skills[0].Id); break;
            case 4: vm.AgentsVM.OpenById(data.Agents[0].Id); break;
            case 5: vm.MemoriesVM.EditMemoryCommand.Execute(data.Memories[0]); break;
            case 6: vm.McpServersVM.OpenById(data.McpServers[0].Id); break;
        }
        await Settle();
        var existing = CurrentEditor(vm, page);
        Assert.NotNull(existing.Id);
        Assert.True(existing.IsEditing);

        IRelayCommand create = page switch
        {
            2 => vm.ProjectsVM.NewProjectCommand,
            3 => vm.SkillsVM.NewSkillCommand,
            4 => vm.AgentsVM.NewAgentCommand,
            5 => vm.MemoriesVM.NewMemoryCommand,
            6 => vm.McpServersVM.NewServerCommand,
            _ => throw new ArgumentOutOfRangeException(nameof(page)),
        };
        create.Execute(null);
        await Settle();
        Assert.Equal((null, true), CurrentEditor(vm, page));

        Assert.True(await vm.TryNavigateHistoryAsync(-1));
        Assert.Equal(existing, CurrentEditor(vm, page));
        Assert.True(await vm.TryNavigateHistoryAsync(-1));
        Assert.Equal((null, false), CurrentEditor(vm, page));

        Assert.True(await vm.TryNavigateHistoryAsync(1));
        Assert.Equal(existing, CurrentEditor(vm, page));
        Assert.True(await vm.TryNavigateHistoryAsync(1));
        Assert.Equal((null, true), CurrentEditor(vm, page));
    });

    [Fact]
    public Task CrossPageLinks_AreOneDestination_AndKeepTheUnchangedEditorDraft() => Run(async (vm, data) =>
    {
        vm.SelectedNavIndex = MainViewModel.MemoriesNavIndex;
        await Settle();
        var memory = data.Memories[0];
        memory.Scope = MemoryScopes.Project;
        memory.ProjectId = data.Projects[0].Id;
        vm.MemoriesVM.EditMemoryCommand.Execute(memory);
        await Settle();
        vm.MemoriesVM.EditContent = "An unsaved edit";

        vm.MemoriesVM.OpenProjectCommand.Execute(null);
        await Settle();
        Assert.Equal(2, vm.SelectedNavIndex);
        Assert.Same(data.Projects[0], vm.ProjectsVM.SelectedProject);

        Assert.True(await vm.TryNavigateHistoryAsync(-1));
        Assert.Equal(MainViewModel.MemoriesNavIndex, vm.SelectedNavIndex);
        Assert.Same(memory, vm.MemoriesVM.SelectedMemory);
        Assert.Equal("An unsaved edit", vm.MemoriesVM.EditContent);
        Assert.True(vm.MemoriesVM.HasUnsavedChanges);

        Assert.True(await vm.TryNavigateHistoryAsync(1));
        Assert.Equal(2, vm.SelectedNavIndex);
        Assert.Same(data.Projects[0], vm.ProjectsVM.SelectedProject);
    });

    [Fact]
    public Task Discover_RestoresFiltersSourcesAndCardDetails() => Run(async (vm, _) =>
    {
        vm.SelectedNavIndex = MainViewModel.DiscoverNavIndex;
        await Settle();
        vm.DiscoverVM.ApplyPlugins(
            [new PluginListing("test-helper", "market", "A helper", "1.0", null, null, null, null, null, [], null)], []);
        vm.DiscoverVM.ShowAllCommand.Execute(null);
        var card = Assert.Single(vm.DiscoverVM.PluginPreview);

        vm.DiscoverVM.ShowPluginsCommand.Execute(null);
        await Settle();
        card.OpenCommand.Execute(null);
        await Settle();
        vm.DiscoverVM.ShowSourcesCommand.Execute(null);
        await Settle();

        Assert.True(await vm.TryNavigateHistoryAsync(-1));
        Assert.Equal(DiscoverFilter.Plugins, vm.DiscoverVM.Filter);
        Assert.Same(card, vm.DiscoverVM.OpenCard);
        Assert.True(vm.DiscoverVM.IsViewingDetail);
        Assert.True(await vm.TryNavigateHistoryAsync(-1));
        Assert.Equal(DiscoverFilter.Plugins, vm.DiscoverVM.Filter);
        Assert.False(vm.DiscoverVM.IsViewingDetail);
        Assert.True(await vm.TryNavigateHistoryAsync(-1));
        Assert.Equal(DiscoverFilter.All, vm.DiscoverVM.Filter);
        Assert.True(await vm.TryNavigateHistoryAsync(1));
        Assert.True(await vm.TryNavigateHistoryAsync(1));
        Assert.Same(card, vm.DiscoverVM.OpenCard);
        Assert.True(await vm.TryNavigateHistoryAsync(1));
        Assert.Equal(DiscoverFilter.Sources, vm.DiscoverVM.Filter);
        Assert.False(vm.DiscoverVM.IsViewingDetail);

        vm.DiscoverVM.ShowPluginsCommand.Execute(null);
        await Settle();
        vm.DiscoverVM.SearchQuery = "test";
        await Settle();
        vm.DiscoverVM.SearchQuery = "test-helper";
        await Settle();
        Assert.True(await vm.TryNavigateHistoryAsync(-1));
        Assert.Equal(DiscoverFilter.Plugins, vm.DiscoverVM.Filter);
        Assert.Equal("", vm.DiscoverVM.SearchQuery);
        Assert.True(await vm.TryNavigateHistoryAsync(1));
        Assert.Equal("test-helper", vm.DiscoverVM.SearchQuery);
    });

    [Fact]
    public Task Library_RestoresCollectionFiltersAndThePreview() => Run(async (vm, data) =>
    {
        data.Chats[0].Messages[0].Attachments =
            [Path.Combine(Path.GetTempPath(), "navigation-photo.png"), Path.Combine(Path.GetTempPath(), "navigation-report.pdf")];
        await vm.LibraryVM.EnsureLoadedAsync();
        vm.SelectedNavIndex = MainViewModel.LibraryNavIndex;
        await Settle();
        vm.LibraryVM.Kinds.Single(option => option.Id == nameof(LibraryArtifactKind.Image)).SelectCommand.Execute(null);
        await Settle();
        var item = Assert.Single(vm.LibraryVM.Items);
        item.SelectCommand.Execute(null);
        await Settle();

        vm.LibraryVM.SearchQuery = "repor";
        await Settle();
        vm.LibraryVM.SearchQuery = "report";
        await Settle();
        Assert.Empty(vm.LibraryVM.Items);
        Assert.True(await vm.TryNavigateHistoryAsync(-1));
        Assert.Same(item, vm.LibraryVM.SelectedItem);
        Assert.Equal("", vm.LibraryVM.SearchQuery);
        Assert.Single(vm.LibraryVM.Items);

        Assert.True(await vm.TryNavigateHistoryAsync(-1));
        Assert.Null(vm.LibraryVM.SelectedItem);
        Assert.True(vm.LibraryVM.HasActiveFilters);
        Assert.True(await vm.TryNavigateHistoryAsync(-1));
        Assert.False(vm.LibraryVM.HasActiveFilters);
        Assert.Equal(2, vm.LibraryVM.Items.Count);
        Assert.True(await vm.TryNavigateHistoryAsync(1));
        Assert.True(await vm.TryNavigateHistoryAsync(1));
        Assert.Same(item, vm.LibraryVM.SelectedItem);
        Assert.True(item.IsSelected);
        Assert.Single(vm.LibraryVM.Items);
        Assert.True(await vm.TryNavigateHistoryAsync(1));
        Assert.Equal("report", vm.LibraryVM.SearchQuery);
        Assert.Null(vm.LibraryVM.SelectedItem);
        Assert.Empty(vm.LibraryVM.Items);
    });

    [Fact]
    public Task ReturningToAnUnchangedLibrary_DoesNotRebuildItsRows() => Run(async (vm, data) =>
    {
        data.Chats[0].Messages[0].Attachments = [Path.Combine(Path.GetTempPath(), "navigation-photo.png")];
        await vm.LibraryVM.EnsureLoadedAsync();
        vm.SelectedNavIndex = MainViewModel.LibraryNavIndex;
        await Settle();
        var item = Assert.Single(vm.LibraryVM.Items);
        item.SelectCommand.Execute(null);
        await Settle();
        vm.OpenSettingsCommand.Execute(null);
        await Settle();

        var resets = 0;
        vm.LibraryVM.Items.CollectionChanged += (_, _) => resets++;
        Assert.True(await vm.TryNavigateHistoryAsync(-1));
        await Settle();
        Assert.Equal(0, resets);
        Assert.Same(item, vm.LibraryVM.SelectedItem);
    });

    [Fact]
    public Task Jobs_RestoreTheSelectedJobAndNewEditor() => Run(async (vm, data) =>
    {
        vm.SelectedNavIndex = 1;
        await Settle();
        vm.JobsVM.SelectedJob = data.BackgroundJobs[1];
        await Settle();
        vm.JobsVM.NewJobCommand.Execute(null);
        await Settle();

        Assert.True(await vm.TryNavigateHistoryAsync(-1));
        Assert.Same(data.BackgroundJobs[1], vm.JobsVM.SelectedJob);
        Assert.True(await vm.TryNavigateHistoryAsync(-1));
        Assert.Same(data.BackgroundJobs[0], vm.JobsVM.SelectedJob);
        Assert.True(await vm.TryNavigateHistoryAsync(1));
        Assert.True(await vm.TryNavigateHistoryAsync(1));
        Assert.Null(vm.JobsVM.SelectedJob);
        Assert.True(vm.JobsVM.IsEditing);
    });

    [Fact]
    public Task ChatHistory_StillRestoresChatsAndProjectFilters() => Run(async (vm, data) =>
    {
        Assert.True(await vm.OpenChatByIdAsync(data.Chats[0].Id));
        await Settle();
        vm.SelectedProjectFilter = data.Projects[0].Id;
        await Settle();
        Assert.True(await vm.OpenChatByIdAsync(data.Chats[1].Id));
        await Settle();
        vm.OpenSettingsCommand.Execute(null);
        await Settle();

        Assert.True(await vm.TryNavigateHistoryAsync(-1));
        Assert.Equal(0, vm.SelectedNavIndex);
        Assert.Equal(data.Chats[1].Id, vm.ActiveChatId);
        Assert.True(await vm.TryNavigateHistoryAsync(-1));
        Assert.Equal(data.Chats[0].Id, vm.ActiveChatId);
        Assert.Null(vm.SelectedProjectFilter);
        Assert.True(await vm.TryNavigateHistoryAsync(1));
        Assert.Equal(data.Chats[1].Id, vm.ActiveChatId);
        Assert.Equal(data.Projects[0].Id, vm.SelectedProjectFilter);
    });

    [Fact]
    public Task ProjectFilter_IsRememberedWhenTheSelectedChatDoesNotChange() => Run(async (vm, data) =>
    {
        Assert.True(await vm.OpenChatByIdAsync(data.Chats[1].Id));
        await Settle();
        vm.SelectedProjectFilter = data.Projects[0].Id;
        await Settle();
        Assert.Equal(data.Chats[1].Id, vm.ActiveChatId);

        vm.OpenSettingsCommand.Execute(null);
        await Settle();
        Assert.True(await vm.TryNavigateHistoryAsync(-1));
        Assert.Equal(data.Projects[0].Id, vm.SelectedProjectFilter);
        Assert.True(await vm.TryNavigateHistoryAsync(-1));
        Assert.Equal(data.Chats[1].Id, vm.ActiveChatId);
        Assert.Null(vm.SelectedProjectFilter);
    });

    [Fact]
    public Task DeletedItems_AreSkipped_AndANewDestinationClearsForwardHistory() => Run(async (vm, data) =>
    {
        vm.SelectedNavIndex = MainViewModel.MemoriesNavIndex;
        await Settle();
        vm.MemoriesVM.EditMemoryCommand.Execute(data.Memories[0]);
        await Settle();
        vm.SettingsVM.SelectedPageIndex = 0;
        await Settle();
        data.Memories.RemoveAt(0);

        Assert.True(await vm.TryNavigateHistoryAsync(-1));
        Assert.Equal(MainViewModel.MemoriesNavIndex, vm.SelectedNavIndex);
        Assert.False(vm.MemoriesVM.IsEditing);
        vm.SelectedNavIndex = 2;
        await Settle();
        Assert.False(await vm.TryNavigateHistoryAsync(1));
    });

    [Fact]
    public Task MouseButtonsAndHistoryKeys_WorkOutsideChat_WithoutClickingThePage() => Run(async (vm, _) =>
    {
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 860 };
        window.Show();
        try
        {
            vm.OpenSettingsCommand.Execute(null);
            await Settle();
            vm.SettingsVM.SelectedPageIndex = 3;
            await Settle();
            var point = new Point(1000, 120);
            window.MouseDown(point, MouseButton.XButton1);
            window.MouseUp(point, MouseButton.XButton1);
            await Settle();
            Assert.Equal(7, vm.SelectedNavIndex);
            Assert.Equal(0, vm.SettingsVM.SelectedPageIndex);

            window.MouseDown(point, MouseButton.XButton2);
            window.MouseUp(point, MouseButton.XButton2);
            await Settle();
            Assert.Equal(3, vm.SettingsVM.SelectedPageIndex);

            var back = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.BrowserBack };
            window.RaiseEvent(back);
            await Settle();
            Assert.True(back.Handled);
            Assert.Equal(0, vm.SettingsVM.SelectedPageIndex);

            var forward = new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Right,
                KeyModifiers = KeyModifiers.Alt,
            };
            window.RaiseEvent(forward);
            await Settle();
            Assert.True(forward.Handled);
            Assert.Equal(3, vm.SettingsVM.SelectedPageIndex);

            vm.SearchOverlayVM.Open();
            window.MouseDown(point, MouseButton.XButton1);
            window.MouseUp(point, MouseButton.XButton1);
            await Settle();
            Assert.True(vm.SearchOverlayVM.IsOpen);
            Assert.Equal(3, vm.SettingsVM.SelectedPageIndex);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void ExternalDetails_ReopenByStableKeyAfterTheCatalogChanges()
    {
        var external = new ExternalCapabilitiesViewModel(CapabilityKind.Agent);
        var descriptor = new CapabilityDescriptor
        {
            Kind = CapabilityKind.Agent,
            Name = "reporter",
            Origin = CapabilityOrigin.Personal,
            Content = "Report findings.",
        };
        external.Update(new CapabilitySnapshot(CapabilityQuery.Empty, [descriptor], isComplete: true));
        external.Open(Assert.Single(Assert.Single(external.Groups).Items));
        var key = Assert.IsType<string>(external.NavigationItemKey);
        external.Close();
        external.Update(new CapabilitySnapshot(CapabilityQuery.Empty, [descriptor with { Description = "Updated" }], isComplete: true));

        Assert.True(external.ContainsNavigationItem(key));
        external.OpenNavigationItem(key);
        Assert.Equal("Updated", external.OpenItem?.Description);

        external.Update(new CapabilitySnapshot(CapabilityQuery.Empty, [], isComplete: true));
        Assert.False(external.ContainsNavigationItem(key));
    }

    private static (Guid? Id, bool IsEditing) CurrentEditor(MainViewModel vm, int page) => page switch
    {
        2 => (vm.ProjectsVM.SelectedProject?.Id, vm.ProjectsVM.IsEditing),
        3 => (vm.SkillsVM.SelectedSkill?.Id, vm.SkillsVM.IsEditing),
        4 => (vm.AgentsVM.SelectedAgent?.Id, vm.AgentsVM.IsEditing),
        5 => (vm.MemoriesVM.SelectedMemory?.Id, vm.MemoriesVM.IsEditing),
        6 => (vm.McpServersVM.SelectedServer?.Id, vm.McpServersVM.IsEditing),
        _ => throw new ArgumentOutOfRangeException(nameof(page)),
    };

    private static async Task Settle()
        => await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);

    private static async Task Run(Func<MainViewModel, AppData, Task> test)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var data = new AppData
            {
                Settings = new UserSettings
                {
                    IsOnboarded = true, AutoSaveChats = false, EnableMemoryAutoSave = false, McpRegistryUrls = [],
                },
                Projects = [new Project { Name = "Project" }],
                Skills = [new Skill { Name = "Skill", Content = "Help." }],
                Agents = [new LumiAgent { Name = "Agent" }],
                Memories = [new Memory { Key = "Memory", Category = "Work" }],
                McpServers = [new McpServer { Name = "Connector", Command = "test" }],
                BackgroundJobs = [new BackgroundJob { Name = "First", IsEnabled = false }, new BackgroundJob { Name = "Second", IsEnabled = false }],
                Chats =
                [
                    new Chat { Title = "First", Messages = [new ChatMessage { Role = "user", Content = "First" }] },
                    new Chat { Title = "Second", Messages = [new ChatMessage { Role = "user", Content = "Second" }] },
                ],
            };
            data.Chats[1].ProjectId = data.Projects[0].Id;
            var skillsDirectory = Path.Combine(Path.GetTempPath(), $"lumi-navigation-{Guid.NewGuid():N}");
            using var vm = new MainViewModel(new DataStore(data, skillsDirectory), TestCopilot.Shared,
                new UpdateService(), startBackgroundJobs: false, initializeCopilotOnStartup: false);
            try
            {
                await test(vm, data);
            }
            finally
            {
                vm.Dispose();
                if (Directory.Exists(skillsDirectory))
                    Directory.Delete(skillsDirectory, recursive: true);
            }
        }, CancellationToken.None);
    }
}
